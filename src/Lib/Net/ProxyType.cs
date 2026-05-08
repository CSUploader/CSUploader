// <copyright file="ProxyType.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// Supported proxy protocols. Stored as the integer value on
/// <see cref="Dal.ProxySettingDbm.Type"/>.
/// </summary>
public enum ProxyType
{
    /// <summary>Direct connection — no proxy.</summary>
    None = 0,
    Http = 1,
    Https = 2,
    Socks4 = 3,
    Socks5 = 4,
}
