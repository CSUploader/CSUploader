// <copyright file="Settings.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

public class AppSettings
{
    public static int DefaultUploadsTabPageRefreshTimer { get; } = 1;

    public static int DefaultMaxConcurrentCPUJobs { get; } = 1;

    public static int DefaultMaxConcurrentUploadJobs { get; } = 5;

    public static int DefaultMaxUploadsPerHost { get; } = 1;

    /// <summary>
    /// Deliberately below what the parallel-capable hosters declare (8). Degree multiplies with
    /// <see cref="DefaultMaxConcurrentUploadJobs"/>: at its default of 5, a ceiling of 8 would mean
    /// 40 in-flight part bodies at once.
    /// </summary>
    public static int DefaultMaxParallelPartsPerFile { get; } = 4;

    public static RemoveFinishedUploadsMode DefaultRemoveFinishedUploads { get; } = RemoveFinishedUploadsMode.Never;

    public static string DefaultGridFontFamily { get; } = "Tahoma";

    public static double DefaultGridFontSize { get; } = 12;

    public static bool DefaultIsDarkMode { get; } = false;

    public static IfFileExistsBehavior DefaultIfFileExists { get; } = IfFileExistsBehavior.Ask;

    public static AutostartUploadsMode DefaultAutostartUploads { get; } = AutostartUploadsMode.OnlyIfRunningAtLastSession;

    /// <summary>
    /// Empty string means "auto-detect from <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
    /// on first launch". After the user picks a language explicitly, this holds a BCP-47
    /// tag like "en", "zh-Hans", "ko", "ja".
    /// </summary>
    public static string DefaultLanguage { get; } = string.Empty;

    public static bool DefaultMinimizeToTray { get; } = false;

    public static CloseAction DefaultCloseAction { get; } = CloseAction.Ask;

    public static bool DefaultAutoDisableFailingProxies { get; } = true;

    // OFF by default: a fresh install has no working proxy, and defaulting "Use Proxies" on would route
    // (or try to route) every request through a non-existent proxy. Users opt in via Connection Manager.
    public static bool DefaultProxiesEnabled { get; } = false;

    public static bool DefaultShowCompletionToasts { get; } = true;

    /// <summary>
    /// Whether the startup update check happens IN FRONT of the main window — behind a splash — or
    /// behind it.
    /// </summary>
    /// <remarks>
    /// Off is not "do not check". The app opens straight away and a quiet check follows once the
    /// window is up, so an installed build still reports an update in the title bar and Help →
    /// Install Update still installs it; the six-hourly poll is unaffected either way. What off buys
    /// is the absence of the splash and of anything asking a question before the app opens.
    /// </remarks>
    public static bool DefaultCheckForUpdatesAtStartup { get; } = true;

    /// <summary>
    /// Whether an update found by the startup check installs itself without asking.
    /// </summary>
    /// <remarks>
    /// <b>Default OFF, and deliberately the timid one.</b> Installing hands over to Velopack, which
    /// replaces the app and restarts the process — so the cost of defaulting this wrong is a user
    /// who launched CSUploader and got a restart they never agreed to. Off keeps the existing
    /// behaviour: the update is offered and the user decides.
    /// <para>
    /// Only meaningful while <see cref="CheckForUpdatesAtStartup"/> is on, since it describes what to
    /// do with what THAT check finds. The quiet post-startup check never auto-installs — it exists
    /// precisely for people who did not want startup touched — and the gated path re-reads the
    /// hydrated parent setting before acting, so a stale "on" cannot install behind an owner who
    /// turned startup checks off.
    /// </para>
    /// </remarks>
    public static bool DefaultAutoInstallUpdatesAtStartup { get; } = false;

    /// <summary>Show every hoster — the wizard's list is a catalogue, and pre-filtering it by
    /// default would hide destinations from someone who never asked to.</summary>
    public static HosterAccountFilter DefaultWizardHosterAccountFilter { get; } = HosterAccountFilter.Both;


    public static bool DefaultAllowInvalidServerCertificates { get; } = false;

    // OFF by default, even in DEBUG. When on, EVERY outbound hoster request is rewritten to
    // MockServerBaseUrl (http://localhost:8080), so a fresh DB that defaulted this on silently sent
    // all real sign-in/upload traffic to a mock that usually isn't running ("target machine actively
    // refused it (localhost:8080)"). Developers testing against the mock enable it explicitly.
    public static bool DefaultUseMockServer { get; } = false;

    private int? uploadsTabPageRefreshTimer;
    private int? maxConcurrentCPUJobs;
    private int? maxConcurrentUploadJobs;
    private int? maxUploadsPerHost;
    private int? maxParallelPartsPerFile;

    public int UploadsTabPageRefreshTimer
    {
        get => uploadsTabPageRefreshTimer ?? DefaultUploadsTabPageRefreshTimer;
        set => uploadsTabPageRefreshTimer = value;
    }

    public int MaxConcurrentCPUJobs
    {
        get => maxConcurrentCPUJobs ?? DefaultMaxConcurrentCPUJobs;
        set => maxConcurrentCPUJobs = value;
    }

    public int MaxConcurrentUploadJobs
    {
        get => maxConcurrentUploadJobs ?? DefaultMaxConcurrentUploadJobs;
        set => maxConcurrentUploadJobs = value;
    }

    public bool MaxUploadsPerHostEnabled { get; set; }

    public int MaxUploadsPerHost
    {
        get => maxUploadsPerHost ?? DefaultMaxUploadsPerHost;
        set => maxUploadsPerHost = value;
    }

    /// <summary>
    /// Ceiling on concurrent parts within ONE file, capping whatever a hoster declares. Lets a user
    /// on a metered or fragile link pull every host back to sequential with one setting.
    /// </summary>
    public int MaxParallelPartsPerFile
    {
        get => maxParallelPartsPerFile ?? DefaultMaxParallelPartsPerFile;
        set => maxParallelPartsPerFile = value;
    }

    public RemoveFinishedUploadsMode RemoveFinishedUploads { get; set; } = DefaultRemoveFinishedUploads;

    /// <summary>
    /// Font family applied to the Uploads / Uploaded DataGrids. Bound via the GridFontFamily
    /// dynamic resource so updates propagate live.
    /// </summary>
    public string GridFontFamily { get; set; } = DefaultGridFontFamily;

    /// <summary>
    /// Font size for the Uploads / Uploaded DataGrids. Bound via the GridFontSize
    /// dynamic resource so updates propagate live.
    /// </summary>
    public double GridFontSize { get; set; } = DefaultGridFontSize;

    /// <summary>
    /// User's preferred theme. Loaded at startup so the UI starts in the right mode
    /// instead of flashing light then switching.
    /// </summary>
    public bool IsDarkMode { get; set; } = DefaultIsDarkMode;

    public IfFileExistsBehavior IfFileExists { get; set; } = DefaultIfFileExists;

    private AutostartUploadsMode? autostartUploads;
    private bool forceAutostartNever;

    /// <summary>
    /// Auto-start policy for pending uploads at app launch. <see cref="AutostartUploadsMode.Never"/>
    /// keeps loaded packages idle until the user clicks Start. The getter honours the
    /// <see cref="ForceAutostartUploadsNever"/> latch; the setter always records the user's value
    /// so the Settings UI and DB persistence are unaffected.
    /// </summary>
    public AutostartUploadsMode AutostartUploads
    {
        get => forceAutostartNever ? AutostartUploadsMode.Never : autostartUploads ?? DefaultAutostartUploads;
        set => autostartUploads = value;
    }

    /// <summary>
    /// One-way latch for agent-driven dev sessions (the Avalonia head's --agent switch):
    /// after this call the getter reports Never regardless of later writes, so the
    /// settings-load during MainViewModel.InitializeAsync cannot re-enable autostart before
    /// LoadPersistedPackagesAsync honours it. The setter still records the user's value —
    /// the Settings UI and DB persistence are unaffected.
    /// </summary>
    public void ForceAutostartUploadsNever() => forceAutostartNever = true;

    /// <summary>
    /// Active UI language as a BCP-47 tag (e.g. "en", "zh-Hans", "ko", "ja"). Empty
    /// means "auto-detect from the OS's current UI culture on next launch".
    /// </summary>
    public string Language { get; set; } = DefaultLanguage;

    /// <summary>
    /// When true, minimising the main window hides it into the system tray instead of
    /// the taskbar. The tray icon restores the window on click.
    /// </summary>
    public bool MinimizeToTray { get; set; } = DefaultMinimizeToTray;

    /// <summary>
    /// What the main window's X (close) button does. Defaults to <see cref="CloseAction.Ask"/>
    /// so the first close prompts the user to choose minimise-to-tray or full exit.
    /// </summary>
    public CloseAction CloseAction { get; set; } = DefaultCloseAction;

    /// <summary>
    /// The upload mode the wizard's File Hosters step opens filtered to. Only the STARTING value —
    /// the user can change the filter in the wizard, and "Clear filter" there returns to
    /// <see cref="HosterAccountFilter.Both"/> regardless of this.
    /// </summary>
    public HosterAccountFilter WizardHosterAccountFilter { get; set; } = DefaultWizardHosterAccountFilter;

    /// <summary>
    /// Where the wizard's "Add files…" / "Add folder…" pickers open. Blank — the default — means
    /// reopen wherever the last pick was made (<see cref="LastBrowsedFolder"/>), which is itself
    /// blank until the first pick and then hands the choice to the OS. A value here always wins:
    /// it is for a staging directory everything is uploaded from.
    /// </summary>
    public string DefaultUploadDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The directory the last pick was made in, remembered across restarts and used whenever
    /// <see cref="DefaultUploadDirectory"/> is blank. Bookkeeping the wizard writes, not something
    /// the user sets — it has no Settings row. Recorded even while a default directory is set, so
    /// clearing that box falls straight back to somewhere useful.
    /// </summary>
    public string LastBrowsedFolder { get; set; } = string.Empty;

    /// <summary>
    /// When true, a proxy that fails a connectivity test or an upload is automatically
    /// unticked (Enabled = false) so the rotation skips it. The status icon updates either
    /// way; this flag only controls the auto-uncheck behaviour.
    /// </summary>
    public bool AutoDisableFailingProxies { get; set; } = DefaultAutoDisableFailingProxies;

    /// <summary>
    /// Master switch for the proxy rotation. When false, uploads bypass every configured
    /// proxy and connect directly — the Connection Manager grid and per-proxy Test still
    /// work, so you can curate / verify proxies without committing to using them yet.
    /// </summary>
    public bool ProxiesEnabled { get; set; } = DefaultProxiesEnabled;

    /// <summary>
    /// Master switch for the bottom-right "upload finished" toast popups. When false,
    /// completions are silent (still visible in the Uploaded tab and Logs).
    /// </summary>
    public bool ShowCompletionToasts { get; set; } = DefaultShowCompletionToasts;

    public bool CheckForUpdatesAtStartup { get; set; } = DefaultCheckForUpdatesAtStartup;

    public bool AutoInstallUpdatesAtStartup { get; set; } = DefaultAutoInstallUpdatesAtStartup;

    /// <summary>
    /// When true, the upload pipeline's <see cref="HttpClient"/> instances
    /// accept ANY server certificate without validating the name or chain. Defaults to
    /// false. Intended as an opt-in workaround for hosters whose storage CDN edges (e.g.
    /// FileBoom's <c>cmb-*.filestore.app</c> nodes) ship certs that fail standard
    /// validation (name mismatch, untrusted chain). Turning this on disables the protection
    /// .NET gives you against MITM on every outbound request — only enable when uploads
    /// are otherwise impossible.
    /// </summary>
    public bool AllowInvalidServerCertificates { get; set; } = DefaultAllowInvalidServerCertificates;

    public int? SpeedLimit { get; set; }

    /// <summary>
    /// When true, all outbound file-hoster HTTP requests are rewritten to <see cref="MockServerBaseUrl"/>/&lt;hoster&gt;/...
    /// for testing against a local mock server. Defaults to false in every build (see <see cref="DefaultUseMockServer"/>) —
    /// a dev opts in via Settings → General for a session.
    /// </summary>
    public bool UseMockServer { get; set; } = DefaultUseMockServer;

    public string MockServerBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Confirmation-dialog keys for which the user has ticked "Don't ask me again".
    /// Stored as a comma-separated setting; kept as a HashSet at runtime for O(1) lookup.
    /// </summary>
    public HashSet<string> SuppressedConfirmations { get; } = [with(StringComparer.Ordinal)];
}
