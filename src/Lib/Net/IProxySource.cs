// <copyright file="IProxySource.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// DI seam over <see cref="ProxyManager"/>. Returns a non-null <see cref="ProxyChoice"/>
/// — "no proxy" is <see cref="ProxyChoice.Direct"/>, never null. Lets <see cref="Upload.Pipeline.AttemptRunner"/>
/// take a constructor dependency without reaching into the global <c>ProxyManager.Current</c>.
/// </summary>
public interface IProxySource
{
    ProxyChoice Next();
}
