// <copyright file="ProxySettingDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal;

[Table("ProxySetting")]
public class ProxySettingDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// <see cref="CSUploader.Lib.Net.ProxyType"/> as an int.
    /// </summary>
    public int Type { get; set; }

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lower numbers = higher priority. Used to order the rotation.
    /// </summary>
    public int Priority { get; set; }
}
