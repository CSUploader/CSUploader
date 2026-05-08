// <copyright file="Settings.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;

namespace CSUploader.Upload;

public class AppSettings
{
    public static int DefaultUploadsTabPageRefreshTimer { get; } = 1;

    public static int DefaultMaxConcurrentCPUJobs { get; } = 1;

    public static int DefaultMaxConcurrentUploadJobs { get; } = 5;

    public static int DefaultMaxUploadsPerHost { get; } = 1;

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

    public static bool DefaultProxiesEnabled { get; } = true;

#if DEBUG
    public static bool DefaultUseMockServer { get; } = true;
#else
    public static bool DefaultUseMockServer { get; } = false;
#endif

    public static Regex UrlRegex { get; } = new Regex("(?:https?[:]\\/\\/)?(?:www\\.)?[-a-zA-Z0-9@:%._\\+~#=]{2,256}\\.[a-z]{2,6}\\b(?:[-a-zA-Z0-9@:%_\\+.~#?&//=]*)", RegexOptions.Compiled);

    /// <summary>
    /// Static accessor for code that hasn't been migrated to DI yet. Declared after all
    /// Default* fields because the instance initializer (`= DefaultX`) reads them; if
    /// `Current = new()` ran before those statics, every Default-referencing property
    /// would silently get the type's zero default instead.
    /// Prefer constructor injection of AppSettings where possible.
    /// </summary>
    public static AppSettings Current { get; set; } = new();

    private int? uploadsTabPageRefreshTimer;
    private int? maxConcurrentCPUJobs;
    private int? maxConcurrentUploadJobs;
    private int? maxUploadsPerHost;

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

    /// <summary>
    /// Auto-start policy for pending uploads at app launch. <see cref="AutostartUploadsMode.Never"/>
    /// keeps loaded packages idle until the user clicks Start.
    /// </summary>
    public AutostartUploadsMode AutostartUploads { get; set; } = DefaultAutostartUploads;

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

    public int? SpeedLimit { get; set; }

    /// <summary>
    /// When true, all outbound file-hoster HTTP requests are rewritten to <see cref="MockServerBaseUrl"/>/&lt;hoster&gt;/...
    /// for testing against a local mock server. Defaults to true in DEBUG builds, false in RELEASE.
    /// </summary>
    public bool UseMockServer { get; set; } = DefaultUseMockServer;

    public string MockServerBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Confirmation-dialog keys for which the user has ticked "Don't ask me again".
    /// Stored as a comma-separated setting; kept as a HashSet at runtime for O(1) lookup.
    /// </summary>
    public HashSet<string> SuppressedConfirmations { get; } = new(StringComparer.Ordinal);
}
