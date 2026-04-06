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
    public string CompressionPassword { get; set; } = string.Empty;

    [Required]
    public string FileUrl { get; set; } = string.Empty;

    [Required]
    public string FileHosterName { get; set; } = string.Empty;

    public UploadPackageDbm? Package { get; set; }
}
