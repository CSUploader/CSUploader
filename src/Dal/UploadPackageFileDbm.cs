// <copyright file="UploadPackageFileDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal
{
    [Table("UploadPackageFile")]
    public partial class UploadPackageFileDbm
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public string? FileName { get; set; }

        [Required]
        public string? FileDirectory { get; set; }

        [Required]
        public string? FileSize { get; set; }

        [Required]
        public string? FileHoster { get; set; }

        [Required]
        public DateTime StartDateTime { get; set; }

        [Required]
        public DateTime FInishedDateTime { get; set; }

        [Required]
        public string? CompressionPassword { get; set; }

        [Required]
        public string? FileUrl { get; set; }

        [Required]
        public string? FileHosterName { get; set; }

        public virtual UploadPackageDbm? Package { get; set; }
    }
}
