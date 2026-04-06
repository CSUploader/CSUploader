// <copyright file="Settings.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Extensions;
using System.Text.RegularExpressions;

namespace CSUploader.Upload;

public class AppSettings
{
    /// <summary>
    /// Static accessor for code that hasn't been migrated to DI yet.
    /// Prefer constructor injection of AppSettings where possible.
    /// </summary>
    public static AppSettings Current { get; set; } = new();

    public static string DefaultTempArchiveDirectory { get; } = PathExtensions.GetTemporaryDirectory();

    public static int DefaultUploadsTabPageRefreshTimer { get; } = 1;

    public static int DefaultMaxConcurrentCPUJobs { get; } = 1;

    public static int DefaultMaxConcurrentUploadJobs { get; } = 5;

    public static Regex UrlRegex { get; } = new Regex("(?:https?[:]\\/\\/)?(?:www\\.)?[-a-zA-Z0-9@:%._\\+~#=]{2,256}\\.[a-z]{2,6}\\b(?:[-a-zA-Z0-9@:%_\\+.~#?&//=]*)", RegexOptions.Compiled);

    private string? tempArchiveDirectory;
    private int? uploadsTabPageRefreshTimer;
    private int? maxConcurrentCPUJobs;
    private int? maxConcurrentUploadJobs;

    public string TempArchiveDirectory
    {
        get => tempArchiveDirectory ?? DefaultTempArchiveDirectory;
        set => tempArchiveDirectory = value;
    }

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

    public int? SpeedLimit { get; set; }
}
