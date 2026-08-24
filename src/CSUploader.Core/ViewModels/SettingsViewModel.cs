// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class SettingsViewModel(
    SettingRepository settingRepository,
    AccountManagerViewModel accountManager,
    AppSettings settings,
    IDialogService dialogService,
    IAppLogger logger,
    ITrayIconService? trayIconManager = null,
    IFontEnumerationService? fontEnumerationService = null,
    IThemeApplier? themeApplier = null,
    UploadPackageRepository? uploadPackageRepository = null,
    LogEntryRepository? logEntryRepository = null,
    LogsViewModel? logsViewModel = null,
    Upload.UploadScheduler? uploadScheduler = null) : ObservableObject
{
    private readonly SettingRepository _settingRepository = settingRepository;
    private readonly AppSettings _settings = settings;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IAppLogger _logger = logger;
    private readonly ITrayIconService? _trayIconManager = trayIconManager;
    private readonly IThemeApplier? _themeApplier = themeApplier;

    /// <summary>
    /// The Settings tab's Accounts page, split into its own ViewModel: the grid, add/edit/remove,
    /// verification and Refresh all live there; the accounts section of SettingsView re-points its
    /// DataContext here. Injected (a singleton like this VM) rather than constructed, so the upload
    /// wizard and the head shell reach the SAME account list this page shows.
    /// </summary>
    public AccountManagerViewModel AccountManager { get; } = accountManager;

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
    public partial int MaxConcurrentCPUJobs { get; set; } = AppSettings.DefaultMaxConcurrentCPUJobs;

    [ObservableProperty]
    public partial int MaxConcurrentUploadJobs { get; set; } = AppSettings.DefaultMaxConcurrentUploadJobs;

    [ObservableProperty]
    public partial bool MaxUploadsPerHostEnabled { get; set; }

    [ObservableProperty]
    public partial int MaxUploadsPerHost { get; set; } = AppSettings.DefaultMaxUploadsPerHost;

    [ObservableProperty]
    public partial int MaxParallelPartsPerFile { get; set; } = AppSettings.DefaultMaxParallelPartsPerFile;

    [ObservableProperty]
    public partial RemoveFinishedUploadsMode RemoveFinishedUploads { get; set; } = AppSettings.DefaultRemoveFinishedUploads;

    [ObservableProperty]
    public partial string GridFontFamily { get; set; } = AppSettings.DefaultGridFontFamily;

    [ObservableProperty]
    public partial double GridFontSize { get; set; } = AppSettings.DefaultGridFontSize;

    /// <summary>
    /// All font families installed on the system, sorted by display name. Resolved
    /// via <see cref="IFontEnumerationService"/> so the dropdown reflects whatever
    /// the user currently has installed instead of a curated subset. Instance (not static)
    /// so it can be fed from DI; empty when no font service is supplied (headless tests).
    /// </summary>
    public IReadOnlyList<string> GridFontFamilyOptions { get; } =
        fontEnumerationService?.GetSystemFontFamilyNames() ?? [];

    [ObservableProperty]
    public partial IfFileExistsBehavior IfFileExists { get; set; } = AppSettings.DefaultIfFileExists;

    [ObservableProperty]
    public partial AutostartUploadsMode AutostartUploads { get; set; } = AppSettings.DefaultAutostartUploads;

    /// <summary>
    /// BCP-47 tag for the active UI language. Bound to the language dropdown on the
    /// General page. Empty means "auto-detect" — only persisted that way pre-first-pick.
    /// </summary>
    /// <remarks>
    /// Hand-rolled (not <c>[ObservableProperty]</c>) so the setter can reject a null/blank value.
    /// The language ComboBox binds <c>SelectedValue</c> two-way with a <c>SelectedValueBinding</c>,
    /// and Avalonia transiently pushes a null back on attach — it matches <c>SelectedValue</c> before
    /// the value binding resolves, coerces to null, and (TwoWay) writes it to the source. A generated
    /// setter would store that null, blanking the dropdown and crashing <see cref="OnLanguageChanged"/>'s
    /// <c>new CultureInfo(null)</c>. Ignoring it keeps the current language; the ComboBox re-selects it
    /// once the value binding is in place. (The enum pickers accept the same null harmlessly — the
    /// null→enum conversion just fails — so only this string picker needed the guard.)
    /// </remarks>
    public string Language
    {
        get;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == field)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(Language));
            OnLanguageChanged(value);
        }
    } = "en";

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; } = AppSettings.DefaultMinimizeToTray;

    [ObservableProperty]
    public partial CloseAction CloseAction { get; set; } = AppSettings.DefaultCloseAction;

    [ObservableProperty]
    public partial bool SpeedLimitEnabled { get; set; }

    [ObservableProperty]
    public partial int SpeedLimitValue { get; set; }

    // ── Upload wizard settings ──

    /// <summary>Which upload mode the wizard's File Hosters step opens filtered to. The starting
    /// value only — the wizard's own filter still moves freely, and its Clear returns to Both.</summary>
    [ObservableProperty]
    public partial HosterAccountFilter WizardHosterAccountFilter { get; set; }
        = AppSettings.DefaultWizardHosterAccountFilter;

    /// <summary>Where the wizard's file/folder pickers open. Blank means "reopen where I last was".</summary>
    [ObservableProperty]
    public partial string DefaultUploadDirectory { get; set; } = string.Empty;

    /// <summary>Picks the default directory with the same native dialog the wizard uses. Cancel
    /// leaves the current value alone rather than blanking it — clearing is what the box is for.</summary>
    [RelayCommand]
    private async Task BrowseForDefaultUploadDirectoryAsync()
    {
        string? picked = await _dialogService.BrowseFolderAsync(
            string.IsNullOrWhiteSpace(DefaultUploadDirectory) ? null : DefaultUploadDirectory,
            Loc("Settings_Upload_DefaultUploadDirectory_DialogTitle"));

        if (!string.IsNullOrWhiteSpace(picked))
        {
            DefaultUploadDirectory = picked;
        }
    }

    // ── Notification settings ──

    [ObservableProperty]
    public partial bool ShowCompletionToasts { get; set; } = AppSettings.DefaultShowCompletionToasts;

    [ObservableProperty]
    public partial bool CheckForUpdatesAtStartup { get; set; } = AppSettings.DefaultCheckForUpdatesAtStartup;

    [ObservableProperty]
    public partial bool AutoInstallUpdatesAtStartup { get; set; } = AppSettings.DefaultAutoInstallUpdatesAtStartup;

    // ── Developer settings ──

    [ObservableProperty]
    public partial bool UseMockServer { get; set; } = AppSettings.DefaultUseMockServer;

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

    /// <summary>Same three options the wizard's own filter bar offers, same strings — this picks
    /// which one it OPENS on.</summary>
    public LocalizedOption<HosterAccountFilter>[] WizardHosterAccountFilterOptions { get; } =
    [
        new(HosterAccountFilter.Both, "Wizard_Step2_FilterAccountBoth"),
        new(HosterAccountFilter.AnonymousOnly, "Wizard_Step2_FilterAnonymous"),
        new(HosterAccountFilter.AccountOnly, "Wizard_Step2_FilterAccountOnly"),
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

    // ── Navigation ──

    [ObservableProperty]
    public partial int SelectedCategoryIndex { get; set; }

    public ObservableCollection<SuppressedConfirmationItem> ConfirmationPrompts { get; } = [];

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

        // Captured raw (may be null/empty) rather than pushed through the Language setter, which
        // rejects blanks — an empty saved tag must still reach PickSupportedLanguage for OS
        // auto-detect (a legacy DB can hold an empty Language row, "pre-first-pick").
        string? savedLanguage = null;

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

                case var k when k == SettingKey.MaxParallelPartsPerFile:
                    if (int.TryParse(setting.Value, out int parallelParts))
                    {
                        MaxParallelPartsPerFile = parallelParts;
                    }

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
                    savedLanguage = setting.Value;
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

                case var k when k == SettingKey.DefaultUploadDirectory:
                    DefaultUploadDirectory = setting.Value ?? string.Empty;
                    break;

                // No ObservableProperty for this one: it is bookkeeping the wizard writes after each
                // pick, not a preference, so it goes straight to AppSettings with no Settings row.
                case var k when k == SettingKey.LastBrowsedFolder:
                    _settings.LastBrowsedFolder = setting.Value ?? string.Empty;
                    break;

                case var k when k == SettingKey.WizardHosterAccountFilter:
                    if (Enum.TryParse(setting.Value, out HosterAccountFilter accountFilter))
                    {
                        WizardHosterAccountFilter = accountFilter;
                    }

                    break;

                case var k when k == SettingKey.CheckForUpdatesAtStartup:
                    // A value that parses, or nothing. StartupUpdatePreference - which decides
                    // whether the splash appears at all, before this runs - treats an unrecognised
                    // value as "unknown" and falls back to the default. Mapping it to false here
                    // would leave the two disagreeing: the splash would appear on every launch
                    // while Settings said the feature was off, and nothing would ever repair it.
                    // The SAME parser the pre-window read uses, not an equivalent one:
                    // bool.TryParse accepts " false " and that one does not, which would recreate
                    // the very disagreement sharing it exists to prevent.
                    if (StartupUpdatePreference.Parse(setting.Value) is { } checkAtStartup)
                    {
                        CheckForUpdatesAtStartup = checkAtStartup;
                    }

                    break;

                case var k when k == SettingKey.AutoInstallUpdatesAtStartup:
                    // The same parser, though nothing reads this one before the window exists, so
                    // there is no second reader to disagree with. Shared anyway, because an
                    // unrecognised value ought to mean the same thing wherever it is stored: fall
                    // back to the default rather than guess.
                    if (StartupUpdatePreference.Parse(setting.Value) is { } autoInstall)
                    {
                        AutoInstallUpdatesAtStartup = autoInstall;
                    }

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
        _settings.MaxParallelPartsPerFile = MaxParallelPartsPerFile;
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
        _settings.CheckForUpdatesAtStartup = CheckForUpdatesAtStartup;
        _settings.AutoInstallUpdatesAtStartup = AutoInstallUpdatesAtStartup;
        _settings.WizardHosterAccountFilter = WizardHosterAccountFilter;
        _settings.DefaultUploadDirectory = DefaultUploadDirectory;

        // Resolve the active UI language: saved value → fallback to OS detection if blank.
        // Display the resolved tag on the dropdown so it always reflects what's in effect.
        string resolved = Localizer.PickSupportedLanguage(savedLanguage);
        Language = resolved;
        _settings.Language = resolved;
        Localizer.Instance.Culture = new CultureInfo(resolved);

        ApplyGridFontResources();

        // Load accounts (the account manager owns the grid now; loading stays part of the
        // Settings hydration so MainViewModel.InitializeAsync keeps its single entry point).
        await AccountManager.LoadAccountsAsync(cancellationToken);

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
    /// Pushes the current grid font settings into the app resources (via
    /// <see cref="IThemeApplier"/>) so that <c>DynamicResource</c> bindings on the DataGrids
    /// pick up the change live. No-op when no applier is supplied (headless tests).
    /// </summary>
    private void ApplyGridFontResources() => _themeApplier?.ApplyGridFont(GridFontFamily, GridFontSize);

    // ── Auto-save partial-method hooks ──
    // Every editable property persists immediately on change (no Save button). The
    // _suppressAutoSave guard short-circuits writes during LoadAsync so hydrating the
    // VM doesn't round-trip through the DB.

    // The four capacity hooks below also KICK the scheduler (Reschedule): FillSlots reads the caps live but
    // only runs on scheduler events, so without the kick a RAISED cap wouldn't launch extra uploads until the
    // next event (typically an upload finishing). With it, the new limit takes effect immediately.

    partial void OnMaxConcurrentCPUJobsChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxConcurrentCPUJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentCPUJobs, value.ToString(CultureInfo.InvariantCulture));
        uploadScheduler?.Reschedule();
    }

    partial void OnMaxConcurrentUploadJobsChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxConcurrentUploadJobs = value;
        _ = AutoSaveAsync(SettingKey.MaxConcurrentUploadJobs, value.ToString(CultureInfo.InvariantCulture));
        uploadScheduler?.Reschedule();
    }

    partial void OnMaxUploadsPerHostEnabledChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxUploadsPerHostEnabled = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHostEnabled, value ? "true" : "false");
        uploadScheduler?.Reschedule();
    }

    partial void OnMaxParallelPartsPerFileChanged(int value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _settings.MaxParallelPartsPerFile = value;
        _ = AutoSaveAsync(SettingKey.MaxParallelPartsPerFile, value.ToString(CultureInfo.InvariantCulture));

        // No Reschedule(): this bounds parallelism WITHIN a file, which each attempt resolves when
        // it starts. Unlike the per-host cap it does not change which files the scheduler launches.
    }

    partial void OnMaxUploadsPerHostChanged(int value)
    {
        if (_suppressAutoSave)
            return;
        _settings.MaxUploadsPerHost = value;
        _ = AutoSaveAsync(SettingKey.MaxUploadsPerHost, value.ToString(CultureInfo.InvariantCulture));
        uploadScheduler?.Reschedule();
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

    partial void OnDefaultUploadDirectoryChanged(string value)
    {
        if (_suppressAutoSave)
            return;
        _settings.DefaultUploadDirectory = value;
        _ = AutoSaveAsync(SettingKey.DefaultUploadDirectory, value);
    }

    partial void OnWizardHosterAccountFilterChanged(HosterAccountFilter value)
    {
        if (_suppressAutoSave)
            return;
        _settings.WizardHosterAccountFilter = value;
        _ = AutoSaveAsync(SettingKey.WizardHosterAccountFilter, value.ToString());
    }

    partial void OnCheckForUpdatesAtStartupChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.CheckForUpdatesAtStartup = value;
        _ = AutoSaveAsync(SettingKey.CheckForUpdatesAtStartup, value ? "true" : "false");
    }

    partial void OnAutoInstallUpdatesAtStartupChanged(bool value)
    {
        if (_suppressAutoSave)
            return;
        _settings.AutoInstallUpdatesAtStartup = value;
        _ = AutoSaveAsync(SettingKey.AutoInstallUpdatesAtStartup, value ? "true" : "false");
    }

    /// <summary>
    /// Sets the startup-prompt preference and WAITS for it to reach the database.
    /// </summary>
    /// <remarks>
    /// The property setter above auto-saves fire-and-forget, which is right for a user ticking a
    /// box in Settings and wrong for the startup prompt: choosing "Update now" hands straight over
    /// to Velopack, which exits the process, so an unawaited write can lose the preference the user
    /// just expressed. This is the same single write through the same path — the setter's own
    /// auto-save is suppressed so it does not become two.
    /// </remarks>
    public async Task SetCheckForUpdatesAtStartupAsync(bool value, CancellationToken cancellationToken = default)
    {
        bool previous = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            CheckForUpdatesAtStartup = value;
            _settings.CheckForUpdatesAtStartup = value;
        }
        finally
        {
            _suppressAutoSave = previous;
        }

        await SaveSettingAsync(SettingKey.CheckForUpdatesAtStartup, value ? "true" : "false", cancellationToken);
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

    private void OnLanguageChanged(string value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

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

    private Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
        => _settingRepository.UpsertAsync(key, value, cancellationToken);
}
