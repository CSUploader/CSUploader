// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class SettingsViewModel(
    SettingRepository settingRepository,
    FileHosterLoginRepository accountRepository,
    AppSettings settings,
    IDialogService dialogService,
    IAppLogger logger,
    TrayIconManager? trayIconManager = null,
    UploadPackageRepository? uploadPackageRepository = null) : ObservableObject
{
    private readonly SettingRepository _settingRepository = settingRepository;
    private readonly FileHosterLoginRepository _accountRepository = accountRepository;
    private readonly AppSettings _settings = settings;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IAppLogger _logger = logger;
    private readonly TrayIconManager? _trayIconManager = trayIconManager;
    // Optional so existing tests that don't exercise the Database section don't have to
    // construct an upload-package repo. Wired by DI in the real app.
    private readonly UploadPackageRepository? _uploadPackageRepository = uploadPackageRepository;

    // Suppresses auto-save while LoadAsync is hydrating ObservableProperty values from the
    // DB — otherwise every "real" load would round-trip back through SaveSettingAsync.
    private bool _suppressAutoSave;

    private static string Loc(string key) => Localizer.Instance[key];

    private static string LocF(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Localizer.Instance[key], args);

    // ── Upload settings ──

    [ObservableProperty]
    private int maxConcurrentCPUJobs = AppSettings.DefaultMaxConcurrentCPUJobs;

    [ObservableProperty]
    private int maxConcurrentUploadJobs = AppSettings.DefaultMaxConcurrentUploadJobs;

    [ObservableProperty]
    private bool maxUploadsPerHostEnabled;

    [ObservableProperty]
    private int maxUploadsPerHost = AppSettings.DefaultMaxUploadsPerHost;

    [ObservableProperty]
    private RemoveFinishedUploadsMode removeFinishedUploads = AppSettings.DefaultRemoveFinishedUploads;

    [ObservableProperty]
    private string gridFontFamily = AppSettings.DefaultGridFontFamily;

    [ObservableProperty]
    private double gridFontSize = AppSettings.DefaultGridFontSize;

    /// <summary>
    /// All font families installed on the system, sorted by display name. Resolved
    /// via <see cref="Fonts.SystemFontFamilies"/> so the dropdown reflects whatever
    /// the user currently has installed instead of a curated subset.
    /// </summary>
    public static string[] GridFontFamilyOptions { get; } = [.. Fonts.SystemFontFamilies
        .Select(f => f.Source)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];

    [ObservableProperty]
    private IfFileExistsBehavior ifFileExists = AppSettings.DefaultIfFileExists;

    [ObservableProperty]
    private AutostartUploadsMode autostartUploads = AppSettings.DefaultAutostartUploads;

    /// <summary>
    /// BCP-47 tag for the active UI language. Bound to the language dropdown on the
    /// General page. Empty means "auto-detect" — only persisted that way pre-first-pick.
    /// </summary>
    [ObservableProperty]
    private string language = "en";

    [ObservableProperty]
    private bool minimizeToTray = AppSettings.DefaultMinimizeToTray;

    [ObservableProperty]
    private CloseAction closeAction = AppSettings.DefaultCloseAction;

    [ObservableProperty]
    private bool speedLimitEnabled;

    [ObservableProperty]
    private int speedLimitValue;

    // ── Developer settings ──

    [ObservableProperty]
    private bool useMockServer = AppSettings.DefaultUseMockServer;

#pragma warning disable CA1822
    public LocalizedOption<RemoveFinishedUploadsMode>[] RemoveFinishedUploadsOptions { get; } =
    [
        new(RemoveFinishedUploadsMode.Never, "Settings_Upload_RemoveFinished_Never"),
        new(RemoveFinishedUploadsMode.Immediately, "Settings_Upload_RemoveFinished_Immediately"),
        new(RemoveFinishedUploadsMode.AtStartup, "Settings_Upload_RemoveFinished_AtStartup"),
        new(RemoveFinishedUploadsMode.WhenPackageIsReady, "Settings_Upload_RemoveFinished_WhenPackageReady"),
    ];

    public LocalizedOption<IfFileExistsBehavior>[] IfFileExistsOptions { get; } =
    [
        new(IfFileExistsBehavior.Ask, "Settings_Upload_IfExists_Ask"),
        new(IfFileExistsBehavior.Skip, "Settings_Upload_IfExists_Skip"),
        new(IfFileExistsBehavior.Overwrite, "Settings_Upload_IfExists_Overwrite"),
        new(IfFileExistsBehavior.Rename, "Settings_Upload_IfExists_Rename"),
    ];

    public LocalizedOption<AutostartUploadsMode>[] AutostartUploadsOptions { get; } =
    [
        new(AutostartUploadsMode.Always, "Settings_Upload_Autostart_Always"),
        new(AutostartUploadsMode.OnlyIfRunningAtLastSession, "Settings_Upload_Autostart_OnlyIfRunning"),
        new(AutostartUploadsMode.Never, "Settings_Upload_Autostart_Never"),
    ];

    /// <summary>
    /// Language picker options. Display names are in the language's own script so the
    /// user can recognise their language before the rest of the UI is translated —
    /// these stay literal (not pulled from the active resx) on purpose.
    /// </summary>
    public sealed record LanguageEntry(string Value, string Label);

    public LanguageEntry[] LanguageOptions { get; } =
    [
        new("en", "English"),
        new("zh-Hans", "中文 (简体)"),
        new("ko", "한국어"),
        new("ja", "日本語"),
        new("vi", "Tiếng Việt"),
        new("fil", "Filipino"),
    ];

    public LocalizedOption<CloseAction>[] CloseActionOptions { get; } =
    [
        new(CloseAction.Ask, "Settings_General_CloseAction_Ask"),
        new(CloseAction.MinimizeToTray, "Settings_General_CloseAction_MinToTray"),
        new(CloseAction.Exit, "Settings_General_CloseAction_Exit"),
    ];
#pragma warning restore CA1822

    // ── Navigation ──

    [ObservableProperty]
    private int selectedCategoryIndex;

    // ── Account management ──

    [ObservableProperty]
    private FileHosterLoginDto? selectedAccount;

    [ObservableProperty]
    private string newAccountHoster = string.Empty;

    [ObservableProperty]
    private string newAccountUsername = string.Empty;

    [ObservableProperty]
    private string newAccountPassword = string.Empty;

    [ObservableProperty]
    private AccountType newAccountType = AccountType.Free;

    [ObservableProperty]
    private string checkAccountStatus = string.Empty;

    [ObservableProperty]
    private bool isCheckingAccount;

    public ObservableCollection<FileHosterLoginDto> Accounts { get; } = [];

    public ObservableCollection<SuppressedConfirmationItem> ConfirmationPrompts { get; } = [];

    public static string[] AvailableHosters => [.. FileHosterClient.FileHosters.Keys];

#pragma warning disable CA1822
    public AccountType[] AccountTypes => [AccountType.Free, AccountType.Premium];
#pragma warning restore CA1822

    // ── Load ──

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _suppressAutoSave = true;
        try
        {
            await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        // Load settings
        SettingDto[] settings = await _settingRepository.GetAllAsync(cancellationToken);

        foreach (SettingDto setting in settings)
        {
            switch (setting.Key)
            {
                case var k when k == SettingKey.MaxConcurrentCPUJobs:
                    if (int.TryParse(setting.Value, out int cpuJobs))
                    {
                        MaxConcurrentCPUJobs = cpuJobs;
                    }

                    break;

                case var k when k == SettingKey.MaxConcurrentUploadJobs:
                    if (int.TryParse(setting.Value, out int uploadJobs))
                    {
                        MaxConcurrentUploadJobs = uploadJobs;
                    }

                    break;

                case var k when k == SettingKey.MaxUploadsPerHostEnabled:
                    MaxUploadsPerHostEnabled = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;

                case var k when k == SettingKey.MaxUploadsPerHost:
                    if (int.TryParse(setting.Value, out int perHost))
                    {
                        MaxUploadsPerHost = perHost;
                    }

                    break;

                case var k when k == SettingKey.RemoveFinishedUploads:
                    if (Enum.TryParse(setting.Value, out RemoveFinishedUploadsMode removeMode))
                    {
                        RemoveFinishedUploads = removeMode;
                    }

                    break;

                case var k when k == SettingKey.GridFontFamily:
                    if (!string.IsNullOrWhiteSpace(setting.Value))
                    {
                        GridFontFamily = setting.Value;
                    }

                    break;

                case var k when k == SettingKey.GridFontSize:
                    if (double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0)
                    {
                        GridFontSize = size;
                    }

                    break;

                case var k when k == SettingKey.IfFileExists:
                    if (Enum.TryParse(setting.Value, out IfFileExistsBehavior existsBehavior))
                    {
                        IfFileExists = existsBehavior;
                    }

                    break;

                case var k when k == SettingKey.AutostartUploads:
                    if (Enum.TryParse(setting.Value, out AutostartUploadsMode autostartMode))
                    {
                        AutostartUploads = autostartMode;
                    }

                    break;

                case var k when k == SettingKey.Language:
                    Language = setting.Value ?? string.Empty;
                    break;

                case var k when k == SettingKey.SpeedLimit:
                    if (int.TryParse(setting.Value, out int speedLimit))
                    {
                        SpeedLimitValue = speedLimit;
                        SpeedLimitEnabled = speedLimit > 0;
                    }

                    break;

                case var k when k == SettingKey.UseMockServer:
                    UseMockServer = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;

                case var k when k == SettingKey.MinimizeToTray:
                    MinimizeToTray = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;

                case var k when k == SettingKey.CloseAction:
                    if (Enum.TryParse(setting.Value, out CloseAction parsedCloseAction))
                    {
                        CloseAction = parsedCloseAction;
                    }

                    break;

                case var k when k == SettingKey.AutoDisableFailingProxies:
                    // No corresponding ObservableProperty here — this lives on the Connection
                    // page, but we still hydrate AppSettings so background code paths see the
                    // user's choice from first call.
                    _settings.AutoDisableFailingProxies = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;

                case var k when k == SettingKey.ProxiesEnabled:
                    // Master switch for the proxy rotation; lives on the Connection page.
                    // Hydrate AppSettings so ProxyManager.NextProxy sees the user's choice
                    // even before the Connection Manager VM has loaded its UI state.
                    _settings.ProxiesEnabled = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;

                case var k when k == SettingKey.SuppressedConfirmations:
                    _settings.SuppressedConfirmations.Clear();
                    if (!string.IsNullOrWhiteSpace(setting.Value))
                    {
                        foreach (string part in setting.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            _settings.SuppressedConfirmations.Add(part);
                        }
                    }

                    break;
            }
        }

        _settings.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        _settings.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        _settings.MaxUploadsPerHostEnabled = MaxUploadsPerHostEnabled;
        _settings.MaxUploadsPerHost = MaxUploadsPerHost;
        _settings.RemoveFinishedUploads = RemoveFinishedUploads;
        _settings.GridFontFamily = GridFontFamily;
        _settings.GridFontSize = GridFontSize;
        _settings.IfFileExists = IfFileExists;
        _settings.AutostartUploads = AutostartUploads;
        _settings.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        _settings.UseMockServer = UseMockServer;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.CloseAction = CloseAction;

        // Resolve the active UI language: saved value → fallback to OS detection if blank.
        // Display the resolved tag on the dropdown so it always reflects what's in effect.
        string resolved = Localizer.PickSupportedLanguage(Language);
        Language = resolved;
        _settings.Language = resolved;
        Localizer.Instance.Culture = new CultureInfo(resolved);

        ApplyGridFontResources();

        // Load accounts
        await LoadAccountsAsync(cancellationToken);

        RefreshConfirmationPrompts();
    }

    private void RefreshConfirmationPrompts()
    {
        foreach (SuppressedConfirmationItem item in ConfirmationPrompts)
        {
            item.PropertyChanged -= ConfirmationItem_PropertyChanged;
        }

        ConfirmationPrompts.Clear();
        foreach ((string key, string labelResourceKey) in ConfirmationKeys.All)
        {
            bool suppressed = _settings.SuppressedConfirmations.Contains(key);
            SuppressedConfirmationItem item = new(key, labelResourceKey, askAgain: !suppressed);
            item.PropertyChanged += ConfirmationItem_PropertyChanged;
            ConfirmationPrompts.Add(item);
        }
    }

    /// <summary>
    /// Pushes the current grid font settings into <see cref="System.Windows.Application.Resources"/>
    /// so that <c>DynamicResource</c> bindings on the DataGrids pick up the change live.
    /// </summary>
    private void ApplyGridFontResources()
    {
        System.Windows.Application app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Resources["GridFontFamily"] = new FontFamily(GridFontFamily);
            app.Resources["GridFontSize"] = GridFontSize;
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to apply grid font: {ex.Message}");
        }
    }

    // ── Auto-save partial-method hooks ──
    // Every editable property persists immediately on change (no Save button). The
    // _suppressAutoSave guard short-circuits writes during LoadAsync so hydrating the
    // VM doesn't round-trip through the DB.

    partial void OnMaxConcurrentCPUJobsChanged(int value)
    {
        if (_suppressAutoSave) return;
        _settings.MaxConcurrentCPUJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentCPUJobs, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnMaxConcurrentUploadJobsChanged(int value)
    {
        if (_suppressAutoSave) return;
        _settings.MaxConcurrentUploadJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentUploadJobs, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnMaxUploadsPerHostEnabledChanged(bool value)
    {
        if (_suppressAutoSave) return;
        _settings.MaxUploadsPerHostEnabled = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHostEnabled, value ? "true" : "false");
    }

    partial void OnMaxUploadsPerHostChanged(int value)
    {
        if (_suppressAutoSave) return;
        _settings.MaxUploadsPerHost = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHost, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnRemoveFinishedUploadsChanged(RemoveFinishedUploadsMode value)
    {
        if (_suppressAutoSave) return;
        _settings.RemoveFinishedUploads = value;
        _ = AutoSaveAsync(SettingKey.RemoveFinishedUploads, value.ToString());
    }

    partial void OnGridFontFamilyChanged(string value)
    {
        if (_suppressAutoSave) return;
        _settings.GridFontFamily = value;
        ApplyGridFontResources();
        _ = AutoSaveAsync(SettingKey.GridFontFamily, value);
    }

    partial void OnGridFontSizeChanged(double value)
    {
        if (_suppressAutoSave) return;
        _settings.GridFontSize = value;
        ApplyGridFontResources();
        _ = AutoSaveAsync(SettingKey.GridFontSize, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnIfFileExistsChanged(IfFileExistsBehavior value)
    {
        if (_suppressAutoSave) return;
        _settings.IfFileExists = value;
        _ = AutoSaveAsync(SettingKey.IfFileExists, value.ToString());
    }

    partial void OnAutostartUploadsChanged(AutostartUploadsMode value)
    {
        if (_suppressAutoSave) return;
        _settings.AutostartUploads = value;
        _ = AutoSaveAsync(SettingKey.AutostartUploads, value.ToString());
    }

    partial void OnSpeedLimitEnabledChanged(bool value)
    {
        if (_suppressAutoSave) return;
        _settings.SpeedLimit = value ? SpeedLimitValue : null;
        _ = AutoSaveAsync(SettingKey.SpeedLimit, value ? SpeedLimitValue.ToString(CultureInfo.InvariantCulture) : "0");
    }

    partial void OnSpeedLimitValueChanged(int value)
    {
        if (_suppressAutoSave) return;
        if (SpeedLimitEnabled)
        {
            _settings.SpeedLimit = value;
        }

        _ = AutoSaveAsync(SettingKey.SpeedLimit, SpeedLimitEnabled ? value.ToString(CultureInfo.InvariantCulture) : "0");
    }

    partial void OnUseMockServerChanged(bool value)
    {
        if (_suppressAutoSave) return;
        _settings.UseMockServer = value;
        _ = AutoSaveAsync(SettingKey.UseMockServer, value ? "true" : "false");
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppressAutoSave) return;
        _settings.MinimizeToTray = value;
        _trayIconManager?.UpdateVisibility();
        _ = AutoSaveAsync(SettingKey.MinimizeToTray, value ? "true" : "false");
    }

    partial void OnCloseActionChanged(CloseAction value)
    {
        if (_suppressAutoSave) return;
        _settings.CloseAction = value;
        _trayIconManager?.UpdateVisibility();
        _ = AutoSaveAsync(SettingKey.CloseAction, value.ToString());
    }

    partial void OnLanguageChanged(string value)
    {
        if (_suppressAutoSave) return;
        _settings.Language = value;
        // Apply immediately so the open Settings page (and every other tab) re-renders
        // in the new language without a restart. Open dialogs keep whatever language
        // they captured at construction.
        try
        {
            Localizer.Instance.Culture = new CultureInfo(value);
        }
        catch (CultureNotFoundException ex)
        {
            _logger.Log(this, LogType.Error, $"Unknown language '{value}': {ex.Message}");
        }

        _ = AutoSaveAsync(SettingKey.Language, value);
    }

    private async Task AutoSaveAsync(string key, string value)
    {
        try
        {
            await SaveSettingAsync(key, value, default);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to auto-save '{key}': {ex.Message}");
        }
    }

    private async void ConfirmationItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SuppressedConfirmationItem.AskAgain) || sender is not SuppressedConfirmationItem item)
        {
            return;
        }

        if (item.AskAgain)
        {
            _settings.SuppressedConfirmations.Remove(item.Key);
        }
        else
        {
            _settings.SuppressedConfirmations.Add(item.Key);
        }

        try
        {
            await SaveSettingAsync(SettingKey.SuppressedConfirmations, string.Join(",", _settings.SuppressedConfirmations), default);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to save suppressed-confirmations setting: {ex.Message}");
        }
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken = default)
    {
        Accounts.Clear();
        FileHosterLoginDto[] accounts = await _accountRepository.GetAllAsync(cancellationToken);
        foreach (FileHosterLoginDto account in accounts)
        {
            Accounts.Add(account);
        }
    }

    // ── Commands ──
    // The "Save Settings" button is gone — every edit on the General/Upload pages
    // auto-persists via the Onx­Changed partials above. The Connection page still has
    // its own Save (handled by ConnectionManagerViewModel).

    [RelayCommand]
    private async Task ClearDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_uploadPackageRepository is null)
        {
            return;
        }

        if (!_dialogService.ShowConfirmation(
                Loc("Settings_General_Database_ConfirmMessage"),
                Loc("Settings_General_Database_ConfirmTitle")))
        {
            return;
        }

        try
        {
            (int filesDeleted, int packagesDeleted) =
                await _uploadPackageRepository.DeleteHiddenHistoryAsync(cancellationToken);

            if (filesDeleted == 0 && packagesDeleted == 0)
            {
                _logger.Log(this, LogType.Status, Loc("Settings_General_Database_Status_NothingToClear"));
            }
            else
            {
                _logger.Log(this, LogType.Status, LocF("Settings_General_Database_Status_Cleared_Format", filesDeleted, packagesDeleted));
            }
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Clear database failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddAccountDialog()
    {
        // Open EditAccountWindow in "add" mode with empty fields
        FileHosterLoginDto newAccount = new()
        {
            FileHosterName = AvailableHosters.FirstOrDefault() ?? string.Empty,
            AccountType = AccountType.Free,
        };

        var dialog = new Views.EditAccountWindow(newAccount, AvailableHosters)
        {
            Title = Loc("EditAccount_AddTitle"),
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() == true)
        {
            _ = AddAccountFromDialogAsync(dialog.Result);
        }
    }

    private async Task AddAccountFromDialogAsync(FileHosterLoginDto dto)
    {
        // Auto-check if implementation exists
        var client = FileHosterClient.FindByHost(dto.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is not null)
        {
            IsCheckingAccount = true;
            CheckAccountStatus = Loc("Settings_Accounts_Status_Verifying");

            try
            {
                AccountCheckResult result = await FileHosterClient.CheckAccountAsync(
                    dto.Username ?? string.Empty,
                    dto.Password ?? string.Empty);

                if (result.IsValid)
                {
                    dto.AccountType = result.AccountType;
                    dto.StatusMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK");
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Verified_Format", result.Message);
                }
                else
                {
                    dto.StatusMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed");
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Warning_Format", result.Message);
                }
            }
            catch (Exception ex)
            {
                CheckAccountStatus = LocF("Settings_Accounts_Status_CheckError_Format", ex.Message);
            }
            finally
            {
                IsCheckingAccount = false;
            }
        }

        Dictionary<int, string> statuses = BuildStatusMap();
        string newStatus = dto.StatusMessage;

        await _accountRepository.InsertAsync(dto);
        CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", dto.FileHosterName);
        await LoadAccountsAsync();
        ApplyStatusMap(statuses);

        // Set status on the newly added account
        foreach (FileHosterLoginDto a in Accounts)
        {
            if (a.FileHosterName == dto.FileHosterName && a.Username == dto.Username && !statuses.ContainsKey(a.Id))
            {
                a.StatusMessage = newStatus;
                UpdateAccountStatus(a.Id, newStatus);
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (Accounts.Count == 0)
        {
            CheckAccountStatus = Loc("Settings_Accounts_Status_NoAccountsToRefresh");
            return;
        }

        IsCheckingAccount = true;
        int checked_ = 0;
        int updated = 0;

        Dictionary<int, string> statuses = BuildStatusMap();

        foreach (FileHosterLoginDto account in Accounts.ToArray())
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_CheckingProgress_Format", account.Username, account.FileHosterName, ++checked_, Accounts.Count);
            UpdateAccountStatus(account.Id, Loc("Settings_Accounts_Status_CheckingShort"));
            await Task.Yield();

            var client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
            if (client is null)
            {
                statuses[account.Id] = Loc("Settings_Accounts_Status_NoImpl");
                UpdateAccountStatus(account.Id, Loc("Settings_Accounts_Status_NoImpl"));
                continue;
            }

            try
            {
                AccountCheckResult result = await FileHosterClient.CheckAccountAsync(
                    account.Username ?? string.Empty,
                    account.Password ?? string.Empty,
                    cancellationToken);

                if (result.IsValid)
                {
                    statuses[account.Id] = result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK");
                    if (account.AccountType != result.AccountType)
                    {
                        account.AccountType = result.AccountType;
                        updated++;
                    }
                }
                else
                {
                    statuses[account.Id] = result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed");
                }

                await _accountRepository.UpdateAsync(account, cancellationToken);
            }
            catch (Exception ex)
            {
                statuses[account.Id] = LocF("Settings_Accounts_Status_Error_Format", ex.Message);
            }

            UpdateAccountStatus(account.Id, statuses[account.Id]);
        }

        IsCheckingAccount = false;
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        CheckAccountStatus = LocF("Settings_Accounts_Status_RefreshSummary_Format", checked_, updated);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CheckAccountAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewAccountHoster) || string.IsNullOrWhiteSpace(NewAccountUsername))
        {
            _dialogService.ShowError(Loc("Settings_Accounts_Validation_FillHosterUser"));
            return;
        }

        IsCheckingAccount = true;
        CheckAccountStatus = Loc("Settings_Accounts_Status_Checking");

        try
        {
            var client = FileHosterClient.FindByHost(NewAccountHoster, Protocol.Http, _logger);
            if (client is null)
            {
                CheckAccountStatus = LocF("Settings_Accounts_Status_NoImplWillSave_Format", NewAccountHoster);
                return;
            }

            AccountCheckResult result = await FileHosterClient.CheckAccountAsync(NewAccountUsername, NewAccountPassword, cancellationToken);

            if (result.IsValid)
            {
                NewAccountType = result.AccountType;
                CheckAccountStatus = LocF("Settings_Accounts_Status_ValidExclaim_Format", result.Message);
            }
            else
            {
                CheckAccountStatus = LocF("Settings_Accounts_Status_Failed_Format", result.Message);
            }
        }
        catch (Exception ex)
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_Error_Format", ex.Message);
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewAccountHoster)
            || string.IsNullOrWhiteSpace(NewAccountUsername)
            || string.IsNullOrWhiteSpace(NewAccountPassword))
        {
            _dialogService.ShowError(Loc("Settings_Accounts_Validation_FillHosterUser"));
            return;
        }

        // Auto-check if a client implementation exists
        var client = FileHosterClient.FindByHost(NewAccountHoster, Protocol.Http, _logger);
        if (client is not null)
        {
            IsCheckingAccount = true;
            CheckAccountStatus = Loc("Settings_Accounts_Status_Verifying");

            try
            {
                AccountCheckResult result = await FileHosterClient.CheckAccountAsync(NewAccountUsername, NewAccountPassword, cancellationToken);
                if (result.IsValid)
                {
                    NewAccountType = result.AccountType;
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Verified_Format", result.Message);
                }
                else
                {
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Warning_Format", result.Message);
                    if (!_dialogService.ShowConfirmation(LocF("Settings_Accounts_Check_FailedAddAnyway_Format", result.Message), Loc("Settings_Accounts_Check_DialogTitle")))
                    {
                        IsCheckingAccount = false;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                CheckAccountStatus = LocF("Settings_Accounts_Status_CheckError_Format", ex.Message);
                if (!_dialogService.ShowConfirmation(LocF("Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format", ex.Message), Loc("Settings_Accounts_Check_DialogTitle")))
                {
                    IsCheckingAccount = false;
                    return;
                }
            }
            finally
            {
                IsCheckingAccount = false;
            }
        }

        FileHosterLoginDto dto = new()
        {
            FileHosterName = NewAccountHoster,
            Username = NewAccountUsername,
            Password = NewAccountPassword,
            AccountType = NewAccountType,
        };

        await _accountRepository.InsertAsync(dto, cancellationToken);

        CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", NewAccountHoster);
        NewAccountUsername = string.Empty;
        NewAccountPassword = string.Empty;

        await LoadAccountsAsync(cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveAccountAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        if (!_dialogService.ShowOptOutConfirmation(
                ConfirmationKeys.RemoveFileHosterAccount,
                LocF("Settings_Accounts_Remove_Message_Format", SelectedAccount.Username, SelectedAccount.FileHosterName),
                Loc("Settings_Accounts_Remove_Title")))
        {
            return;
        }

        await _accountRepository.DeleteAsync(SelectedAccount.Id, cancellationToken);
        await LoadAccountsAsync(cancellationToken);
    }

    [RelayCommand]
    private void EditAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        // Open edit dialog
        var dialog = new Views.EditAccountWindow(SelectedAccount, AvailableHosters)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            // Save changes
            _ = SaveEditedAccountAsync(dialog.Result);
        }
    }

    private async Task SaveEditedAccountAsync(FileHosterLoginDto updated)
    {
        await _accountRepository.UpdateAsync(updated);
        await LoadAccountsAsync();
    }

    // AllowConcurrentExecutions on every async-but-context-menu command on this VM —
    // CommunityToolkit's default AsyncRelayCommand makes CanExecute=!IsRunning, so a
    // single hung Rapidgator API call would leave the context-menu entries permanently
    // greyed out for the rest of the session. Save/Add stay non-concurrent because
    // they'd otherwise double-insert.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        var client = FileHosterClient.FindByHost(SelectedAccount.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is null)
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_NoImpl_Format", SelectedAccount.FileHosterName);
            return;
        }

        // Capture before UI updates can reset SelectedAccount
        FileHosterLoginDto account = SelectedAccount;
        int accountId = account.Id;
        string username = account.Username ?? string.Empty;
        string password = account.Password ?? string.Empty;

        IsCheckingAccount = true;
        CheckAccountStatus = LocF("Settings_Accounts_Status_CheckingProgress_Format", username, account.FileHosterName, 1, 1);

        // Show "Checking..." in the DataGrid immediately and yield to let UI render
        UpdateAccountStatus(accountId, Loc("Settings_Accounts_Status_CheckingShort"));
        await Task.Yield();

        try
        {
            AccountCheckResult result = await FileHosterClient.CheckAccountAsync(username, password, cancellationToken);

            string statusMsg;
            if (result.IsValid)
            {
                account.AccountType = result.AccountType;
                statusMsg = result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK");
                CheckAccountStatus = LocF("Settings_Accounts_Status_Valid_Format", result.Message);
            }
            else
            {
                statusMsg = result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed");
                CheckAccountStatus = LocF("Settings_Accounts_Status_Failed_Format", result.Message);
            }

            await _accountRepository.UpdateAsync(account, cancellationToken);

            // Reload and preserve status messages
            Dictionary<int, string> statuses = BuildStatusMap();
            statuses[accountId] = statusMsg;
            await LoadAccountsAsync(cancellationToken);
            ApplyStatusMap(statuses);
        }
        catch (Exception ex)
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_Error_Format", ex.Message);
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAccountAsync(string? parameter, CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        bool disable = !string.Equals(parameter, "Enable", StringComparison.Ordinal);
        string username = SelectedAccount.Username ?? Loc("Common_Unknown");
        SelectedAccount.Disabled = disable;
        await _accountRepository.UpdateAsync(SelectedAccount, cancellationToken);

        Dictionary<int, string> statuses = BuildStatusMap();
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        CheckAccountStatus = disable
            ? LocF("Settings_Accounts_Status_AccountDisabled_Format", username)
            : LocF("Settings_Accounts_Status_AccountEnabled_Format", username);
    }

    // ── Helpers for preserving StatusMessage across reloads ──

    private Dictionary<int, string> BuildStatusMap()
        => Accounts.ToDictionary(a => a.Id, a => a.StatusMessage);

    private void ApplyStatusMap(Dictionary<int, string> statuses)
    {
        foreach (FileHosterLoginDto a in Accounts)
        {
            if (statuses.TryGetValue(a.Id, out string? msg))
            {
                a.StatusMessage = msg;
            }
        }
    }

    /// <summary>
    /// Updates the StatusMessage on an account in the collection and replaces
    /// the item to trigger the ObservableCollection change notification
    /// (since FileHosterLoginDto doesn't implement INotifyPropertyChanged).
    /// </summary>
    private void UpdateAccountStatus(int accountId, string status)
    {
        for (int i = 0; i < Accounts.Count; i++)
        {
            if (Accounts[i].Id == accountId)
            {
                // Create a shallow copy so ObservableCollection sees a different reference
                // and fires CollectionChanged (same-reference assignment is optimized away)
                FileHosterLoginDto copy = new()
                {
                    Id = Accounts[i].Id,
                    FileHosterName = Accounts[i].FileHosterName,
                    Username = Accounts[i].Username,
                    Password = Accounts[i].Password,
                    AccountType = Accounts[i].AccountType,
                    Disabled = Accounts[i].Disabled,
                    StatusMessage = status,
                };
                Accounts[i] = copy;
                return;
            }
        }
    }

    private async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        SettingDto? existing = await _settingRepository.FindByKeyAsync(key, cancellationToken);

        if (existing is not null)
        {
            existing.Value = value;
            await _settingRepository.UpdateAsync(existing, cancellationToken);
        }
        else
        {
            await _settingRepository.InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
    }
}
