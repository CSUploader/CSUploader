// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
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
    IAccountVerifier accountVerifier,
    TrayIconManager? trayIconManager = null,
    UploadPackageRepository? uploadPackageRepository = null,
    LogEntryRepository? logEntryRepository = null,
    LogsViewModel? logsViewModel = null) : ObservableObject
{
    private readonly SettingRepository _settingRepository = settingRepository;
    private readonly FileHosterLoginRepository _accountRepository = accountRepository;
    private readonly AppSettings _settings = settings;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IAppLogger _logger = logger;
    private readonly TrayIconManager? _trayIconManager = trayIconManager;
    private readonly IAccountVerifier _accountVerifier = accountVerifier;
    // Optional so existing tests that don't exercise the Database section don't have to
    // construct an upload-package repo. Wired by DI in the real app.
    private readonly UploadPackageRepository? _uploadPackageRepository = uploadPackageRepository;
    private readonly LogEntryRepository? _logEntryRepository = logEntryRepository;
    // Optional so Clear logs can also wipe the in-memory Logs-tab listviews. Optional
    // because the existing test fixtures construct SettingsViewModel without one.
    private readonly LogsViewModel? _logsViewModel = logsViewModel;

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

    // ── Notification settings ──

    [ObservableProperty]
    private bool showCompletionToasts = AppSettings.DefaultShowCompletionToasts;

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

    public static string[] AvailableHosters => [.. FileHosterClient.NamesAlphabetical];

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

                case var k when k == SettingKey.ShowCompletionToasts:
                    ShowCompletionToasts = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
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

                case var k when k == SettingKey.AllowInvalidServerCertificates:
                    // Lives on the Connection page; hydrate AppSettings here so the
                    // DefaultHttpHandlerFactory sees the user's choice from the very first
                    // HTTP handler it constructs (CheckAccount Refresh on startup, etc.).
                    _settings.AllowInvalidServerCertificates = string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
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
        _settings.ShowCompletionToasts = ShowCompletionToasts;

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
        if (_suppressAutoSave)
            return;
        _settings.MaxConcurrentCPUJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentCPUJobs, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnMaxConcurrentUploadJobsChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxConcurrentUploadJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentUploadJobs, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnMaxUploadsPerHostEnabledChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxUploadsPerHostEnabled = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHostEnabled, value ? "true" : "false");
    }

    partial void OnMaxUploadsPerHostChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxUploadsPerHost = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHost, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnRemoveFinishedUploadsChanged(RemoveFinishedUploadsMode value)
    {
        if (_suppressAutoSave)
            return;
        _settings.RemoveFinishedUploads = value;
        _ = AutoSaveAsync(SettingKey.RemoveFinishedUploads, value.ToString());
    }

    partial void OnGridFontFamilyChanged(string value)
    {
        if (_suppressAutoSave)
            return;
        _settings.GridFontFamily = value;
        ApplyGridFontResources();
        _ = AutoSaveAsync(SettingKey.GridFontFamily, value);
    }

    partial void OnGridFontSizeChanged(double value)
    {
        if (_suppressAutoSave)
            return;
        _settings.GridFontSize = value;
        ApplyGridFontResources();
        _ = AutoSaveAsync(SettingKey.GridFontSize, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnIfFileExistsChanged(IfFileExistsBehavior value)
    {
        if (_suppressAutoSave)
            return;
        _settings.IfFileExists = value;
        _ = AutoSaveAsync(SettingKey.IfFileExists, value.ToString());
    }

    partial void OnAutostartUploadsChanged(AutostartUploadsMode value)
    {
        if (_suppressAutoSave)
            return;
        _settings.AutostartUploads = value;
        _ = AutoSaveAsync(SettingKey.AutostartUploads, value.ToString());
    }

    partial void OnSpeedLimitEnabledChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.SpeedLimit = value ? SpeedLimitValue : null;
        _ = AutoSaveAsync(SettingKey.SpeedLimit, value ? SpeedLimitValue.ToString(CultureInfo.InvariantCulture) : "0");
    }

    partial void OnSpeedLimitValueChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        if (SpeedLimitEnabled)
        {
            _settings.SpeedLimit = value;
        }

        _ = AutoSaveAsync(SettingKey.SpeedLimit, SpeedLimitEnabled ? value.ToString(CultureInfo.InvariantCulture) : "0");
    }

    partial void OnUseMockServerChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.UseMockServer = value;
        _ = AutoSaveAsync(SettingKey.UseMockServer, value ? "true" : "false");
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MinimizeToTray = value;
        _trayIconManager?.UpdateVisibility();
        _ = AutoSaveAsync(SettingKey.MinimizeToTray, value ? "true" : "false");
    }

    partial void OnShowCompletionToastsChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.ShowCompletionToasts = value;
        _ = AutoSaveAsync(SettingKey.ShowCompletionToasts, value ? "true" : "false");
    }

    partial void OnCloseActionChanged(CloseAction value)
    {
        if (_suppressAutoSave)
            return;
        _settings.CloseAction = value;
        _trayIconManager?.UpdateVisibility();
        _ = AutoSaveAsync(SettingKey.CloseAction, value.ToString());
    }

    partial void OnLanguageChanged(string value)
    {
        if (_suppressAutoSave)
            return;
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
        // Preserve the selected row across the rebuild. SelectedItem binds to SelectedAccount, and
        // Clear()+Add() replaces every DTO instance with a fresh one, so without re-selecting by Id
        // the highlighted row is lost — e.g. after a right-click → Refresh reloads the grid.
        int? selectedId = SelectedAccount?.Id;

        Accounts.Clear();
        FileHosterLoginDto[] accounts = await _accountRepository.GetAllAsync(cancellationToken);
        foreach (FileHosterLoginDto account in accounts)
        {
            Accounts.Add(account);
        }

        if (selectedId is int id)
        {
            FileHosterLoginDto? restored = null;
            foreach (FileHosterLoginDto account in Accounts)
            {
                if (account.Id == id)
                {
                    restored = account;
                    break;
                }
            }

            // Null when the account no longer exists (e.g. removed) — clearing the selection
            // is then the correct outcome.
            SelectedAccount = restored;
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

        if (!await _dialogService.ShowConfirmationAsync(
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
    private async Task ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        if (_logEntryRepository is null)
        {
            return;
        }

        if (!await _dialogService.ShowConfirmationAsync(
                Loc("Settings_General_Database_ConfirmClearLogsMessage"),
                Loc("Settings_General_Database_ConfirmClearLogsTitle")))
        {
            return;
        }

        try
        {
            // Pass DateTime.MaxValue so every persisted entry is older than the cutoff.
            // Reuses the existing trim path instead of inventing a new "delete all" verb.
            int deleted = await _logEntryRepository.DeleteOlderThanAsync(DateTime.MaxValue, cancellationToken);

            // Wipe the in-memory Logs-tab listviews too — the user's intent is "I don't
            // want to see these anymore", which the DB delete alone doesn't satisfy.
            _logsViewModel?.StatusLogs.Clear();
            _logsViewModel?.HttpLogs.Clear();
            _logsViewModel?.ErrorLogs.Clear();
            _logsViewModel?.UILogs.Clear();

            if (deleted == 0)
            {
                _logger.Log(this, LogType.Status, Loc("Settings_General_Database_LogsNothingToClear"));
            }
            else
            {
                _logger.Log(this, LogType.Status, LocF("Settings_General_Database_LogsCleared_Format", deleted));
            }
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Clear logs failed: {ex.Message}");
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

        var dialog = new Views.EditAccountWindow(newAccount, AvailableHosters, InteractiveLoginAsync)
        {
            Title = Loc("EditAccount_AddTitle"),
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() == true && dialog.Result is { } addResult)
        {
            _ = AddAccountFromDialogAsync(addResult);
        }
    }

    /// <summary>
    /// Routes credential verification through the injected <see cref="IAccountVerifier"/>.
    /// </summary>
    private Task<AccountCheckResult> VerifyCredentialsAsync(string hosterName, string username, string password, string? apiKey = null, string? sessionCookie = null, CancellationToken cancellationToken = default)
        => _accountVerifier.CheckAsync(hosterName, username, password, apiKey, sessionCookie, cancellationToken);

    /// <summary>Wall-clock that <see cref="FileHosterLoginDto.MarkRefreshed"/> sites stamp
    /// onto each DTO after a CheckAsync completes. Centralised so tests can compare against
    /// the same primitive the production code uses.</summary>
    private static DateTime NowLocal() => DateTime.Now;

    /// <summary>
    /// Drives the interactive (WebView) sign-in for an XFileSharing-API hoster from the
    /// EditAccountWindow's "Sign in" button. Runs the same verify flow as a no-API-key
    /// account check: pops the captcha WebView, scrapes my_account, derives the API key.
    /// Returned to the dialog so it can store the key + show the result.
    /// </summary>
    private Task<AccountCheckResult> InteractiveLoginAsync(string hosterName)
        => VerifyCredentialsAsync(hosterName, username: string.Empty, password: string.Empty, apiKey: null);

    /// <summary>
    /// Copies any session cookie returned by the verifier onto the credentials DTO so the
    /// next persist round-trip carries it. Currently only Ex-Load populates these fields —
    /// the WebView captures a cookie at credential-check time and we hand it forward so
    /// the first real upload doesn't have to re-pop the WebView. No-op for hosters whose
    /// verifier doesn't supply a cookie.
    /// </summary>
    private static void ApplySessionCookieIfPresent(FileHosterLoginDto target, AccountCheckResult result)
    {
        if (result.SessionCookie is not null)
        {
            target.SessionCookie = result.SessionCookie;
            target.SessionCookieExpiresUtc = result.SessionCookieExpiresUtc;
            target.PinnedProxyId = result.PinnedProxyId;
        }

        // ApiKey is propagated separately from cookies — Ex-Load's verify path returns
        // the API key on the result without setting cookie/pin (it clears them once the
        // key is in hand). Apply unconditionally when present so the U/P → ApiKey
        // upgrade lands on the DTO right after the verifier returns it.
        if (result.ApiKey is not null)
        {
            target.ApiKey = result.ApiKey;
        }

        // API-key verifiers (XFileSharingApi, ExtMatrix) return the account email so we
        // can surface it in the grid. The verifier is the canonical source — API-key
        // hosters never expose a UsernameBox in EditAccountWindow, so any prior value is
        // either null or a stale auto-discovery, both of which the new value supersedes.
        if (!string.IsNullOrEmpty(result.DerivedUsername))
        {
            target.Username = result.DerivedUsername;
        }

        // Storage quota: only overwrite when the verifier surfaced fresh values. A null
        // here means "this hoster doesn't report storage" — DON'T clobber the previously
        // persisted numbers in that case.
        if (result.StorageQuotaBytes is { } quota)
        {
            target.StorageQuotaBytes = quota;
        }
        if (result.StorageUsedBytes is { } used)
        {
            target.StorageUsedBytes = used;
        }
    }

    /// <summary>
    /// Called by <see cref="AddAccountDialog"/> after the dialog returns Save. Exposed as
    /// internal (not private) so the unit test can drive it without a real WPF window —
    /// the dialog wiring is the only WPF dependency in this whole flow.
    /// </summary>
    internal async Task AddAccountFromDialogAsync(FileHosterLoginDto dto)
    {
        // Two-phase add so the grid isn't blank for the ~3s the verifier takes:
        //   1. Insert the row up front with CheckStatus = Checking and reload the
        //      Accounts collection so it shows in the DataGrid immediately.
        //   2. Run the verifier, then UPDATE the row with the real result.
        // Hosters we don't have a pipeline for skip phase 2 entirely — the inserted
        // row gets CheckStatus = Unsupported so the colour converter paints it grey.
        var client = FileHosterClient.FindByHost(dto.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        bool willCheck = client is not null;

        if (willCheck)
        {
            dto.SetCheckStatus(AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
        }
        else
        {
            dto.SetCheckStatus(AccountCheckStatus.Unsupported, Loc("Settings_Accounts_Status_NoImpl"));
        }

        // Stamp the "Added at" time once, at creation. ??= so a value the dialog carried over
        // (it never does on an add) is respected, but a fresh account gets now.
        dto.CreatedDateTime ??= NowLocal();

        // Snapshot existing in-memory (status, message) pairs, insert, reload, restore.
        // Same dance RefreshAllAccountsAsync uses so other accounts' transient verify
        // state survives the round-trip through LoadAccountsAsync (both fields are
        // UI-only — reloading from the DB would otherwise reset them).
        Dictionary<int, RowStatus> statuses = BuildStatusMap();
        await _accountRepository.InsertAsync(dto);
        await LoadAccountsAsync();
        ApplyStatusMap(statuses);

        // The freshly-reloaded row for the new account picked up the DB defaults
        // (NotChecked / "Not checked") since neither field is persisted — stamp our
        // intended (Checking | Unsupported, message) onto it so the colour matches.
        UpdateAccountStatus(dto.Id, dto.CheckStatus, dto.StatusMessage);

        if (!willCheck)
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", dto.FileHosterName);
            return;
        }

        IsCheckingAccount = true;
        CheckAccountStatus = Loc("Settings_Accounts_Status_Verifying");

        AccountCheckStatus finalStatus;
        string finalMessage;
        try
        {
            AccountCheckResult result = await VerifyCredentialsAsync(
                dto.FileHosterName ?? string.Empty,
                dto.Username ?? string.Empty,
                dto.Password ?? string.Empty,
                dto.ApiKey,
                dto.SessionCookie);

            if (result.IsValid)
            {
                dto.AccountType = result.AccountType;
                ApplySessionCookieIfPresent(dto, result);
                finalStatus = AccountCheckStatus.Valid;
                finalMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK");
            }
            else
            {
                // No "Failed: " prefix — CheckStatus drives the cell colour now, so the
                // row text is just the verifier's message (e.g. "Wrong password",
                // "The SSL connection could not be established...").
                finalStatus = AccountCheckStatus.Failed;
                finalMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed");
            }
        }
        catch (Exception ex)
        {
            // Transport/exception failures land in the same Failed bucket as verifier
            // IsValid=false — both are red cells to the user, and the message text
            // explains which.
            finalStatus = AccountCheckStatus.Failed;
            finalMessage = ex.Message;
        }
        finally
        {
            IsCheckingAccount = false;
        }

        // Real verifier outcome (success OR failure) → MarkRefreshed stamps CheckStatus,
        // StatusMessage AND LastRefreshedDateTime atomically; the row's grid column for
        // "Refreshed at" picks this up after Accounts[i] = dto below.
        dto.MarkRefreshed(finalStatus, finalMessage, NowLocal());
        await _accountRepository.UpdateAsync(dto);

        // Replace the in-memory row with the verified DTO so AccountType (which a
        // successful Premium check may have flipped from Free), CheckStatus and
        // StatusMessage all reflect the verifier's result. UpdateAccountStatus alone
        // would leave AccountType stuck at whatever LoadAccountsAsync saw before the
        // verify completed.
        for (int i = 0; i < Accounts.Count; i++)
        {
            if (Accounts[i].Id == dto.Id)
            {
                Accounts[i] = dto;
                break;
            }
        }

        CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", dto.FileHosterName);
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

        Dictionary<int, RowStatus> statuses = BuildStatusMap();

        foreach (FileHosterLoginDto account in Accounts.ToArray())
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_CheckingProgress_Format", account.Username, account.FileHosterName, ++checked_, Accounts.Count);
            UpdateAccountStatus(account.Id, AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
            await Task.Yield();

            var client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
            if (client is null)
            {
                // No FileHosterClient implementation → no verifier round-trip happened →
                // RefreshedAt stays null (we didn't actually try anything).
                statuses[account.Id] = new RowStatus(AccountCheckStatus.Unsupported, Loc("Settings_Accounts_Status_NoImpl"), RefreshedAt: null);
                UpdateAccountStatus(account.Id, AccountCheckStatus.Unsupported, Loc("Settings_Accounts_Status_NoImpl"));
                continue;
            }

            try
            {
                AccountCheckResult result = await VerifyCredentialsAsync(
                    account.FileHosterName ?? string.Empty,
                    account.Username ?? string.Empty,
                    account.Password ?? string.Empty,
                    account.ApiKey,
                    account.SessionCookie,
                    cancellationToken);

                // Single stamp covers both Valid and !Valid branches — we tried, so the
                // timestamp reflects the attempt regardless of outcome.
                DateTime refreshedAt = NowLocal();

                if (result.IsValid)
                {
                    statuses[account.Id] = new RowStatus(
                        AccountCheckStatus.Valid,
                        result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK"),
                        refreshedAt);
                    if (account.AccountType != result.AccountType)
                    {
                        account.AccountType = result.AccountType;
                        updated++;
                    }
                    ApplySessionCookieIfPresent(account, result);
                }
                else
                {
                    // CheckStatus drives the cell colour now — row text is just the
                    // verifier's message, no "Failed: " prefix needed.
                    statuses[account.Id] = new RowStatus(
                        AccountCheckStatus.Failed,
                        result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed"),
                        refreshedAt);
                }

                account.LastRefreshedDateTime = refreshedAt;
                await _accountRepository.UpdateAsync(account, cancellationToken);
            }
            catch (Exception ex)
            {
                // Transport exceptions and verifier IsValid=false both bucket as Failed
                // (red cell). The user sees the message text to distinguish; we don't
                // need a separate "Error" colour.
                DateTime refreshedAt = NowLocal();
                statuses[account.Id] = new RowStatus(AccountCheckStatus.Failed, ex.Message, refreshedAt);
                account.LastRefreshedDateTime = refreshedAt;
                // Persist the timestamp even on transport failure. Swallow secondary DB
                // errors so they don't mask the original verifier exception in the UI.
                try
                { await _accountRepository.UpdateAsync(account, cancellationToken); }
                catch { /* keep the primary failure visible */ }
            }

            RowStatus settled = statuses[account.Id];
            UpdateAccountStatus(account.Id, settled.Status, settled.Message);
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
            await _dialogService.ShowErrorAsync(Loc("Settings_Accounts_Validation_FillHosterUser"));
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

            AccountCheckResult result = await VerifyCredentialsAsync(NewAccountHoster, NewAccountUsername, NewAccountPassword, apiKey: null, cancellationToken: cancellationToken);

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
            await _dialogService.ShowErrorAsync(Loc("Settings_Accounts_Validation_FillHosterUser"));
            return;
        }

        // Auto-check if a client implementation exists
        var client = FileHosterClient.FindByHost(NewAccountHoster, Protocol.Http, _logger);
        // Captured from the verifier when a captcha-gated hoster (Ex-Load) returns a
        // session cookie alongside IsValid. Stamped onto the DTO below so the first
        // upload doesn't re-pop the WebView.
        AccountCheckResult? verifyResult = null;
        if (client is not null)
        {
            IsCheckingAccount = true;
            CheckAccountStatus = Loc("Settings_Accounts_Status_Verifying");

            try
            {
                AccountCheckResult result = await VerifyCredentialsAsync(NewAccountHoster, NewAccountUsername, NewAccountPassword, apiKey: null, cancellationToken: cancellationToken);
                verifyResult = result;
                if (result.IsValid)
                {
                    NewAccountType = result.AccountType;
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Verified_Format", result.Message);
                }
                else
                {
                    CheckAccountStatus = LocF("Settings_Accounts_Status_Warning_Format", result.Message);
                    if (!await _dialogService.ShowConfirmationAsync(LocF("Settings_Accounts_Check_FailedAddAnyway_Format", result.Message), Loc("Settings_Accounts_Check_DialogTitle")))
                    {
                        IsCheckingAccount = false;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                CheckAccountStatus = LocF("Settings_Accounts_Status_CheckError_Format", ex.Message);
                if (!await _dialogService.ShowConfirmationAsync(LocF("Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format", ex.Message), Loc("Settings_Accounts_Check_DialogTitle")))
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
            CreatedDateTime = NowLocal(),
        };
        if (client is not null)
        {
            // Reaching here means we attempted a verifier round-trip (success, failure
            // signal handled by the dialog, OR exception the user dismissed via "Add
            // anyway"). Stamp the "Refreshed at" column unconditionally so the user
            // can tell when we last tried — failure included.
            dto.LastRefreshedDateTime = NowLocal();
        }
        if (verifyResult is not null)
        {
            ApplySessionCookieIfPresent(dto, verifyResult);
        }

        await _accountRepository.InsertAsync(dto, cancellationToken);

        CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", NewAccountHoster);
        NewAccountUsername = string.Empty;
        NewAccountPassword = string.Empty;

        await LoadAccountsAsync(cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        string message = targets.Length == 1
            ? LocF("Settings_Accounts_Remove_Message_Format", targets[0].Username, targets[0].FileHosterName)
            : LocF("Settings_Accounts_Remove_MessageBulk_Format", targets.Length);

        if (!await _dialogService.ShowOptOutConfirmationAsync(
                ConfirmationKeys.RemoveFileHosterAccount,
                message,
                Loc("Settings_Accounts_Remove_Title")))
        {
            return;
        }

        foreach (FileHosterLoginDto account in targets)
        {
            await _accountRepository.DeleteAsync(account.Id, cancellationToken);
        }
        await LoadAccountsAsync(cancellationToken);
    }

    /// <summary>
    /// Coerces a XAML-bound <see cref="IList"/> CommandParameter (DataGrid.SelectedItems
    /// is non-generic) into a typed array snapshot. Returning an empty array on no
    /// selection lets RelayCommand callers do a simple length check rather than handle
    /// null.
    /// </summary>
    private static FileHosterLoginDto[] ResolveAccountTargets(IList? selectedItems)
        => selectedItems is null
            ? []
            : [.. selectedItems.OfType<FileHosterLoginDto>()];

    [RelayCommand]
    private void EditAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        // Open edit dialog
        var dialog = new Views.EditAccountWindow(SelectedAccount, AvailableHosters, InteractiveLoginAsync)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.Result is { } editResult)
        {
            // Save changes
            _ = SaveEditedAccountAsync(editResult);
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
    private async Task RefreshSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        IsCheckingAccount = true;
        try
        {
            for (int i = 0; i < targets.Length; i++)
            {
                // targets come from ResolveAccountTargets → the grid's SelectedItems, which
                // are the live Accounts instances. RefreshSingleAccountAsync already mutates
                // this same instance in place (LastRefreshedDateTime, AccountType, session,
                // Storage*) and persists it.
                FileHosterLoginDto account = targets[i];
                RowStatus settled = await RefreshSingleAccountAsync(account, i + 1, targets.Length, cancellationToken);

                // Apply the final outcome to the SAME live row instance — no reload, so the
                // grid updates in place (DTO is now observable) and the selection/highlight is
                // preserved naturally.
                if (settled.RefreshedAt is { } stamp)
                {
                    account.MarkRefreshed(settled.Status, settled.Message, stamp);
                }
                else
                {
                    account.SetCheckStatus(settled.Status, settled.Message);
                }
            }
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    /// <summary>
    /// Runs one row's verification round-trip, updating the grid's status cell and the
    /// global progress text. Returns the <see cref="RowStatus"/> the caller should drop
    /// into its status map before reloading.
    /// </summary>
    private async Task<RowStatus> RefreshSingleAccountAsync(FileHosterLoginDto account, int oneBasedIndex, int total, CancellationToken cancellationToken)
    {
        var client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is null)
        {
            string noImpl = LocF("Settings_Accounts_Status_NoImpl_Format", account.FileHosterName);
            CheckAccountStatus = noImpl;
            // No verifier ran → no "Refreshed at" stamp.
            return new RowStatus(AccountCheckStatus.Unsupported, noImpl, RefreshedAt: null);
        }

        int accountId = account.Id;
        string username = account.Username ?? string.Empty;
        string password = account.Password ?? string.Empty;

        CheckAccountStatus = LocF("Settings_Accounts_Status_CheckingProgress_Format", username, account.FileHosterName, oneBasedIndex, total);
        UpdateAccountStatus(accountId, AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
        await Task.Yield();

        try
        {
            AccountCheckResult result = await VerifyCredentialsAsync(account.FileHosterName ?? string.Empty, username, password, account.ApiKey, account.SessionCookie, cancellationToken);

            // Single stamp covers Valid / !Valid / catch — we did call the verifier, so
            // the timestamp reflects the attempt regardless of outcome.
            DateTime refreshedAt = NowLocal();
            account.LastRefreshedDateTime = refreshedAt;

            if (result.IsValid)
            {
                account.AccountType = result.AccountType;
                ApplySessionCookieIfPresent(account, result);
                CheckAccountStatus = LocF("Settings_Accounts_Status_Valid_Format", result.Message);
                await _accountRepository.UpdateAsync(account, cancellationToken);
                return new RowStatus(
                    AccountCheckStatus.Valid,
                    result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK"),
                    refreshedAt);
            }

            // CheckStatus drives the cell colour now — the row text is just the
            // verifier's message. The global status bar (CheckAccountStatus) keeps
            // its "Failed: " prefix because it has no colour and needs the prefix
            // to convey outcome.
            CheckAccountStatus = LocF("Settings_Accounts_Status_Failed_Format", result.Message);
            await _accountRepository.UpdateAsync(account, cancellationToken);
            return new RowStatus(
                AccountCheckStatus.Failed,
                result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed"),
                refreshedAt);
        }
        catch (Exception ex)
        {
            // Pre-fix this only updated the global status bar; the row was left stuck on
            // "Checking..." with no indication of failure. Now the row also turns red
            // via CheckStatus = Failed.
            DateTime refreshedAt = NowLocal();
            account.LastRefreshedDateTime = refreshedAt;
            // Persist the timestamp even on transport failure. Swallow secondary DB
            // errors so they don't mask the verifier exception that's about to surface
            // in the row's status cell.
            try
            { await _accountRepository.UpdateAsync(account, cancellationToken); }
            catch { /* keep the primary failure visible */ }
            CheckAccountStatus = LocF("Settings_Accounts_Status_Error_Format", ex.Message);
            return new RowStatus(AccountCheckStatus.Failed, ex.Message, refreshedAt);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task EnableSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
        => ApplyEnabledStateAsync(selectedItems, disable: false, cancellationToken);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DisableSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
        => ApplyEnabledStateAsync(selectedItems, disable: true, cancellationToken);

    private async Task ApplyEnabledStateAsync(IList? selectedItems, bool disable, CancellationToken cancellationToken)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        foreach (FileHosterLoginDto account in targets)
        {
            account.Disabled = disable;
            await _accountRepository.UpdateAsync(account, cancellationToken);
        }

        Dictionary<int, RowStatus> statuses = BuildStatusMap();
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        if (targets.Length == 1)
        {
            string username = targets[0].Username ?? string.Empty;
            CheckAccountStatus = disable
                ? LocF("Settings_Accounts_Status_AccountDisabled_Format", username)
                : LocF("Settings_Accounts_Status_AccountEnabled_Format", username);
        }
        else
        {
            CheckAccountStatus = LocF(
                disable ? "Settings_Accounts_Status_AccountsBulkDisabled_Format" : "Settings_Accounts_Status_AccountsBulkEnabled_Format",
                targets.Length);
        }
    }

    // ── Helpers for preserving check status across reloads ──

    /// <summary>(CheckStatus, StatusMessage, RefreshedAt) triple preserved across a
    /// LoadAccountsAsync round-trip. CheckStatus + StatusMessage are UI-only so they'd
    /// otherwise reset to (NotChecked, "Not checked") on every reload; RefreshedAt is
    /// persisted but the snapshot path lets the in-flight RefreshAll loop replay a
    /// freshly-stamped value without re-reading the DB row first.</summary>
    private readonly record struct RowStatus(AccountCheckStatus Status, string Message, DateTime? RefreshedAt);

    private Dictionary<int, RowStatus> BuildStatusMap()
        => Accounts.ToDictionary(a => a.Id, a => new RowStatus(a.CheckStatus, a.StatusMessage, a.LastRefreshedDateTime));

    private static void ApplyStatusMap(Dictionary<int, RowStatus> statuses, IEnumerable<FileHosterLoginDto> accounts)
    {
        foreach (FileHosterLoginDto a in accounts)
        {
            if (statuses.TryGetValue(a.Id, out RowStatus row))
            {
                if (row.RefreshedAt is { } stamp)
                {
                    // Snapshot came from a real verifier round-trip — replay the timestamp too.
                    a.MarkRefreshed(row.Status, row.Message, stamp);
                }
                else
                {
                    // Snapshot came from a non-verification path (Enable/Disable, RemoveSelected,
                    // or a hoster with no implementation) — don't synthesize a refresh stamp.
                    a.SetCheckStatus(row.Status, row.Message);
                }
            }
        }
    }

    private void ApplyStatusMap(Dictionary<int, RowStatus> statuses) => ApplyStatusMap(statuses, Accounts);

    /// <summary>
    /// Updates an account's <see cref="FileHosterLoginDto.CheckStatus"/> and
    /// <see cref="FileHosterLoginDto.StatusMessage"/> together, IN PLACE on the live
    /// <see cref="Accounts"/> instance. <see cref="FileHosterLoginDto"/> now raises
    /// PropertyChanged for those fields, so mutating the existing item re-renders its row
    /// — no need to replace the item (the old copy-every-field workaround), which also
    /// dropped the selection highlight.
    /// </summary>
    private void UpdateAccountStatus(int accountId, AccountCheckStatus status, string message)
    {
        foreach (FileHosterLoginDto account in Accounts)
        {
            if (account.Id == accountId)
            {
                account.SetCheckStatus(status, message);
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
