// <copyright file="SettingKey.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

public static class SettingKey
{
    public static string TempArchiveDirectory { get; } = "tempArchiveDirectory";

    public static string MaxConcurrentCPUJobs { get; } = "maxConcurrentCPUJobs";

    public static string MaxConcurrentUploadJobs { get; } = "maxConcurrentUploadJobs";

    public static string SpeedLimit { get; } = "speedLimit";
}
