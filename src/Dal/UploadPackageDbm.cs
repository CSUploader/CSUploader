// <copyright file="UploadPackageDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal;

[Table("UploadPackage")]
public class UploadPackageDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedDateTime { get; set; } = DateTime.Now;

    public DateTime? ScheduledStartTime { get; set; }

    public bool IsCompleted { get; set; }

    public int? SpeedLimitKBps { get; set; }

    public int StartMode { get; set; }

    /// <summary>
    /// Five-level upload priority (cast to <see cref="Upload.PackagePriority"/>).
    /// Defaults to 0 (Normal). The scheduler picks files from higher-priority
    /// packages first.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Soft-delete flag for the Uploads tab. Set when the user removes a package from
    /// Uploads — the package's per-file rows are still consulted by the Uploaded tab
    /// (each file has its own <see cref="UploadPackageFileDbm.IsHidden"/> flag for that),
    /// so removing from Uploads doesn't strip the upload from history.
    /// </summary>
    public bool IsRemovedFromUploads { get; set; }

    public ICollection<UploadPackageFileDbm> Files { get; set; } = [];
}
