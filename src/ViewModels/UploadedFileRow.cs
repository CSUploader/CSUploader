// <copyright file="UploadedFileRow.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.ViewModels;

/// <summary>
/// Flat row for the Uploaded DataGrid. Groups by <see cref="PackageName"/>.
/// </summary>
public class UploadedFileRow
{
    public int FileId { get; set; }

    public int PackageId { get; set; }

    public string PackageName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FileDirectory { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public string FileHosterName { get; set; } = string.Empty;

    public DateTime FinishedDateTime { get; set; }

    public string? FileUrl { get; set; }

    public string? FileHash { get; set; }
}
