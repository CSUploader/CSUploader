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

    [Required]
    public string DirectoryPath { get; set; } = string.Empty;

    public int? SpeedLimitKBps { get; set; }

    public int StartMode { get; set; }

    public ICollection<UploadPackageFileDbm> Files { get; set; } = [];
}
