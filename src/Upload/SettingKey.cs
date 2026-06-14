// <copyright file="SettingKey.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

public static class SettingKey
{
    public static string MaxConcurrentCPUJobs { get; } = "maxConcurrentCPUJobs";

    public static string MaxConcurrentUploadJobs { get; } = "maxConcurrentUploadJobs";

    public static string MaxUploadsPerHostEnabled { get; } = "maxUploadsPerHostEnabled";

    public static string MaxUploadsPerHost { get; } = "maxUploadsPerHost";

    public static string RemoveFinishedUploads { get; } = "removeFinishedUploads";

    public static string GridFontFamily { get; } = "gridFontFamily";

    public static string GridFontSize { get; } = "gridFontSize";

    public static string IsDarkMode { get; } = "isDarkMode";

    public static string IfFileExists { get; } = "ifFileExists";

    public static string AutostartUploads { get; } = "autostartUploads";

    public static string SpeedLimit { get; } = "speedLimit";

    public static string UseMockServer { get; } = "useMockServer";

    public static string SuppressedConfirmations { get; } = "suppressedConfirmations";

    public static string MinimizeToTray { get; } = "minimizeToTray";

    public static string CloseAction { get; } = "closeAction";

    public static string AutoDisableFailingProxies { get; } = "autoDisableFailingProxies";

    public static string ProxiesEnabled { get; } = "proxiesEnabled";

    public static string UploadsTabHiddenColumns { get; } = "uploadsTabHiddenColumns";

    public static string UploadedTabHiddenColumns { get; } = "uploadedTabHiddenColumns";

    public static string Language { get; } = "language";

    public static string ShowCompletionToasts { get; } = "showCompletionToasts";

    public static string AllowInvalidServerCertificates { get; } = "allowInvalidServerCertificates";
}
