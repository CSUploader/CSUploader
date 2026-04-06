// <copyright file="PackageJob.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Package job.
/// </summary>
[Flags]
public enum PackageJob
{
    /// <summary>
    /// No job.
    /// </summary>
    None = 0x00,

    /// <summary>
    /// The compression job.
    /// </summary>
    Compression = 0x01,

    /// <summary>
    /// The hashing job.
    /// </summary>
    Hashing = 0x02,

    /// <summary>
    /// The upload job.
    /// </summary>
    Upload = 0x04
}
