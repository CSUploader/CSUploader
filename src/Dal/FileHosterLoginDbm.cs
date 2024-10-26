// <copyright file="FileHosterLoginDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal
{
    [Table("FileHosterLogin")]
    public partial class FileHosterLoginDbm
    {
        public FileHosterLoginDbm()
        {
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public string? FileHosterName { get; set; }

        [Required]
        public string? Username { get; set; }

        [Required]
        public string? Password { get; set; }

        public bool Disabled { get; set; }

        public int AccountType { get; set; }
    }
}
