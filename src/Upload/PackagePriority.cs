// <copyright file="PackagePriority.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Five-level package upload priority. Integer-backed so ordering is a single
/// comparison and persistence is a plain int column. Higher value uploads first;
/// <see cref="UploadScheduler"/> sorts packages by this value (descending) when
/// picking the next file to hash or upload.
/// </summary>
public enum PackagePriority
{
    /// <summary>Will only run after every other priority level is exhausted.</summary>
    Lowest = -2,

    /// <summary>Runs after Normal and above.</summary>
    Low = -1,

    /// <summary>Default. Packages created without an explicit priority land here.</summary>
    Normal = 0,

    /// <summary>Preferred over Normal and below.</summary>
    High = 1,

    /// <summary>Always runs first while non-terminal files exist in this package.</summary>
    Highest = 2,
}
