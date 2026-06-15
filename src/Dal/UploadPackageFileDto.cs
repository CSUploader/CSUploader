// <copyright file="UploadPackageFileDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Dal;

public class UploadPackageFileDto
{
    public int Id { get; set; }

    public string? FileName { get; set; }

    public string? FileDirectory { get; set; }

    public long FileSize { get; set; }

    public string? FileHoster { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime FinishedDateTime { get; set; }

    public string? FileUrl { get; set; }

    public string? FileHosterName { get; set; }

    /// <summary>Denormalized account name the file was uploaded with; null for anonymous uploads
    /// and for history rows persisted before this column existed.</summary>
    public string? FileHosterAccount { get; set; }

    public FileState State { get; set; }

    public string? Error { get; set; }

    public bool IsHashingComplete { get; set; }

    public string? FileHash { get; set; }

    public int FileHosterLoginId { get; set; }

    public int SortOrder { get; set; }

    public int PackageId { get; set; }

    public bool IsHidden { get; set; }

    public bool IsRemovedFromUploads { get; set; }

    public UploadPackageDto? Package { get; set; }
}
