// <copyright file="Settings.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;

namespace CSUploader.Upload;

public class AppSettings
{
    /// <summary>
    /// Static accessor for code that hasn't been migrated to DI yet.
    /// Prefer constructor injection of AppSettings where possible.
    /// </summary>
    public static AppSettings Current { get; set; } = new();

    public static int DefaultUploadsTabPageRefreshTimer { get; } = 1;

    public static int DefaultMaxConcurrentCPUJobs { get; } = 1;

    public static int DefaultMaxConcurrentUploadJobs { get; } = 5;

    public static int DefaultMaxUploadsPerHost { get; } = 1;

    public static RemoveFinishedUploadsMode DefaultRemoveFinishedUploads { get; } = RemoveFinishedUploadsMode.Never;

    public static string DefaultGridFontFamily { get; } = "Tahoma";

    public static double DefaultGridFontSize { get; } = 12;

    public static bool DefaultIsDarkMode { get; } = false;

    public static IfFileExistsBehavior DefaultIfFileExists { get; } = IfFileExistsBehavior.Ask;

#if DEBUG
    public static bool DefaultUseMockServer { get; } = true;
#else
    public static bool DefaultUseMockServer { get; } = false;
#endif

    public static Regex UrlRegex { get; } = new Regex("(?:https?[:]\\/\\/)?(?:www\\.)?[-a-zA-Z0-9@:%._\\+~#=]{2,256}\\.[a-z]{2,6}\\b(?:[-a-zA-Z0-9@:%_\\+.~#?&//=]*)", RegexOptions.Compiled);

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
    /// When true, a successful file is removed from the Uploads tab as soon as it completes.
    /// History on the Uploaded tab is preserved.
    /// </summary>
    public bool AutoRemoveCompletedFiles { get; set; }

    /// <summary>
    /// When true, a package is removed from the Uploads tab as soon as every file in it
    /// has completed successfully. History on the Uploaded tab is preserved.
    /// </summary>
    public bool AutoRemoveCompletedPackages { get; set; }

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
