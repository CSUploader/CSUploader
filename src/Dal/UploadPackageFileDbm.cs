// <copyright file="UploadPackageFileDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal;

[Table("UploadPackageFile")]
public class UploadPackageFileDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public string FileName { get; set; } = string.Empty;

    [Required]
    public string FileDirectory { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [Required]
    public string FileHoster { get; set; } = string.Empty;

    public DateTime StartDateTime { get; set; }

    public DateTime FinishedDateTime { get; set; }

    [Required]
    public string FileUrl { get; set; } = string.Empty;

    [Required]
    public string FileHosterName { get; set; } = string.Empty;

    public int State { get; set; }

    public string? Error { get; set; }

    public bool IsHashingComplete { get; set; }

    /// <summary>
    /// Hex-encoded hash bytes for hosters that hash before upload (Rapidgator → MD5).
    /// Stored so the hash survives restarts and is visible in the UI; null until hashing
    /// completes successfully or for hosters that don't pre-hash.
    /// </summary>
    public string? FileHash { get; set; }

    public int FileHosterLoginId { get; set; }

    public int SortOrder { get; set; }

    public int PackageId { get; set; }

    /// <summary>
    /// Soft-delete flag for the Uploaded tab. Hidden rows are kept in the DB so the history
    /// is preserved, but are filtered out of the Uploaded tab's query.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Soft-delete flag for the Uploads tab. Independent of <see cref="IsHidden"/>:
    /// removing a file from Uploads doesn't affect its visibility in the Uploaded
    /// history and vice-versa. A row with both flags set is invisible to both tabs
    /// but still kept in the DB.
    /// </summary>
    public bool IsRemovedFromUploads { get; set; }

    public UploadPackageDbm? Package { get; set; }
}
