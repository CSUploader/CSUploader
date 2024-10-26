// <copyright file="UploadPackageDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal
{
    [Table("UploadPackage")]
    public partial class UploadPackageDbm
    {
        UploadPackageDbm()
        {
            Files = new HashSet<UploadPackageFileDbm>();
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public string? Name { get; set; }

        public virtual ICollection<UploadPackageFileDbm> Files { get; set; }
    }
}
