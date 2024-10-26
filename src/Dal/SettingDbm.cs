// <copyright file="SettingDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace CSUploader.Dal
{
    [Table("Setting")]
    public partial class SettingDbm
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [NotNull]
        public string? Key { get; set; }

        [Required]
        [NotNull]
        public string? Value { get; set; }
    }
}
