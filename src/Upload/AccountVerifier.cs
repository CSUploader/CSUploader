// <copyright file="AccountVerifier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;

namespace CSUploader.Upload;

/// <summary>
/// Default <see cref="IAccountVerifier"/> implementation. Resolves the pipeline via the
/// registry, pulls the next proxy from <see cref="IProxySource"/> (the same rotation
/// upload traffic uses), builds a one-shot <see cref="HttpHandler"/> through the
/// factory, and delegates the actual round-trip to the pipeline. Network failures are
/// caught here so callers always get an <see cref="AccountCheckResult"/> rather than an
/// exception — the Settings UI surfaces these as warnings.
/// </summary>
public sealed class AccountVerifier(
    IFileHosterRegistry registry,
    IHttpHandlerFactory handlerFactory,
    IProxySource proxySource,
    IAppLogger logger) : IAccountVerifier
{
    public async Task<AccountCheckResult> CheckAsync(string hosterName, string username, string password, string? apiKey = null, CancellationToken ct = default)
    {
        IFileHosterPipeline? pipeline = registry.Find(hosterName);
        if (pipeline is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Account checking not implemented for this hoster.");
        }

        // Honour the "Use Proxies" toggle. ProxyManager returns Direct when the toggle
        // is off (legitimate direct case), but returns null when the toggle is on yet no
        // usable proxy exists — in that case we refuse the check rather than leak the
        // user's real IP to the hoster's login endpoint.
        ProxyChoice? proxy = proxySource.Next();
        if (proxy is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Use Proxies is enabled but no usable proxy is available. Add or enable a proxy in Connection Manager, or turn off Use Proxies in Settings.");
        }

        using HttpHandler handler = handlerFactory.Create(proxy, logger);
        try
        {
            return await pipeline.CheckAccountAsync(username, password, apiKey, handler, proxy, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }
    }
}
