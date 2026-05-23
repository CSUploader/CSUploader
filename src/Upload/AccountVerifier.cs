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
    public async Task<AccountCheckResult> CheckAsync(string hosterName, string username, string password, CancellationToken ct = default)
    {
        IFileHosterPipeline? pipeline = registry.Find(hosterName);
        if (pipeline is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Account checking not implemented for this hoster.");
        }

        // Honour the "Use proxies for uploads" toggle and the configured rotation —
        // ProxyManager.Next() already returns ProxyChoice.Direct when the toggle is
        // off or no enabled proxies exist, so this is safe even on a fresh install.
        ProxyChoice proxy = proxySource.Next();
        using HttpHandler handler = handlerFactory.Create(proxy, logger);
        try
        {
            return await pipeline.CheckAccountAsync(username, password, handler, ct);
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
