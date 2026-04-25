// <copyright file="FileState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Flat state enum for package files, replacing the PackageJob + JobStatus pair.
/// </summary>
public enum FileState
{
    /// <summary>
    /// File is idle and has not been scheduled.
    /// </summary>
    Idle,

    /// <summary>
    /// File is queued for hashing.
    /// </summary>
    HashQueued,

    /// <summary>
    /// File is currently being hashed.
    /// </summary>
    Hashing,

    /// <summary>
    /// File is queued for upload.
    /// </summary>
    UploadQueued,

    /// <summary>
    /// File is currently being uploaded.
    /// </summary>
    Uploading,

    /// <summary>
    /// File upload completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// File operation failed.
    /// </summary>
    Failed,

    /// <summary>
    /// File operation is paused.
    /// </summary>
    Paused,

    /// <summary>
    /// File operation was cancelled.
    /// </summary>
    Cancelled,
}
