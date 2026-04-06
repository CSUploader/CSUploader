// <copyright file="FileHosterLoginDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CSUploader.Upload;

namespace CSUploader.Dal;

[Table("FileHosterLogin")]
public class FileHosterLoginDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string? FileHosterName { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public bool Disabled { get; set; }

    public AccountType AccountType { get; set; }
}
