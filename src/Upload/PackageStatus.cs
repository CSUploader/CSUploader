// <copyright file="PackageStatus.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Upload;

/// <summary>
/// The status of a package file.
/// </summary>
public class PackageStatus
{
    /// <summary>
    /// Gets or sets the job.
    /// </summary>
    /// <value>
    /// The job.
    /// </value>
    public PackageJob Job { get; set; } = PackageJob.None;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    /// <value>
    /// The status.
    /// </value>
    public JobStatus Status { get; set; } = JobStatus.Idle;
}
