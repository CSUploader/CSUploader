// <copyright file="IProxySource.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// DI seam over <see cref="ProxyManager"/>. Lets <see cref="Upload.Pipeline.AttemptRunner"/>
/// take a constructor dependency without reaching into the global <c>ProxyManager.Current</c>.
/// </summary>
/// <remarks>
/// Return-value semantics are deliberately tri-state to distinguish "the user wants direct"
/// from "the user wants a proxy but we couldn't get one":
/// <list type="bullet">
///   <item><see cref="ProxyChoice.Direct"/> — the master Use Proxies toggle is off, OR the
///   rotation deliberately includes a <c>ProxyType.None</c> slot. Caller should connect direct.</item>
///   <item>Any other <see cref="ProxyChoice"/> — caller should route through this proxy.</item>
///   <item><c>null</c> — Use Proxies is on but no usable proxy is available. Caller MUST
///   refuse the operation rather than silently connect direct (security/anonymity guarantee).</item>
/// </list>
/// </remarks>
public interface IProxySource
{
    ProxyChoice? Next();
}
