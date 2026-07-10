// <copyright file="ProxySettingDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Dal;

public class ProxySettingDto
{
    public int Id { get; set; }

    public ProxyType Type { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;

    public int Priority { get; set; }
}
