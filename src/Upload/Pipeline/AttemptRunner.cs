// <copyright file="AttemptRunner.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Outer pipeline orchestrator. One <c>RunAsync</c> call = one upload attempt.
/// Picks proxy → builds handler → invokes hoster pipeline → emits <see cref="AttemptCompleted"/>.
/// </summary>
public sealed class AttemptRunner(IFileHosterRegistry registry, IProxySource proxySource, IHttpHandlerFactory handlerFactory)
{

    /// <summary>
    /// Raised after every attempt. <see cref="ProxyManager"/> subscribes here to update
    /// connectivity icons; the old <c>PackageManager.OnFileStateChanged</c> reach-through
    /// to <c>ProxyManager.Current</c> is replaced by this subscription.
    /// </summary>
    public event EventHandler<AttemptCompleted>? AttemptCompleted;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptInputs inputs, [EnumeratorCancellation] CancellationToken ct)
    {
        ProxyChoice? proxy = proxySource.Next();
        if (proxy is null)
        {
            // Use Proxies is enabled but no usable proxy is available. Refuse the upload
            // rather than silently fall through to a direct connection — the user enabled
            // proxies expecting their real IP to be hidden from the hoster, and shipping
            // bytes direct would violate that expectation.
            const string reason =
                "Upload blocked: Use Proxies is enabled but no usable proxy is available. "
                + "Add or enable a proxy in Connection Manager, or turn off Use Proxies in Settings.";
            yield return new AttemptFailed(reason, null);
            AttemptCompleted noProxy = new(Success: false, ProxyId: 0, FileUrl: null);
            yield return noProxy;
            this.AttemptCompleted?.Invoke(this, noProxy);
            yield break;
        }

        yield return new ProxyPicked(proxy);

        HttpHandler handler = handlerFactory.Create(proxy, inputs.Logger);
        yield return new HandlerBuilt(handler);

        IFileHosterPipeline? pipeline = registry.Find(inputs.HosterName);
        if (pipeline is null)
        {
            string reason = $"No pipeline registered for hoster '{inputs.HosterName}'";
            yield return new AttemptFailed(reason, null);
            AttemptCompleted terminal = new(Success: false, ProxyId: proxy.Id, FileUrl: null);
            yield return terminal;
            this.AttemptCompleted?.Invoke(this, terminal);
            yield break;
        }

        AttemptContext ctx = new()
        {
            AttemptId = Guid.NewGuid(),
            FilePath = inputs.FilePath,
            FileName = inputs.FileName,
            FileSize = inputs.FileSize,
            FileHash = inputs.FileHash,
            HosterName = inputs.HosterName,
            Credentials = inputs.Credentials,
            Proxy = proxy,
            Handler = handler,
            Logger = inputs.Logger,
            SpeedLimitProvider = inputs.SpeedLimitProvider,
            Cancellation = ct,
        };

        bool success = false;
        string? finalUrl = null;
        Exception? failure = null;

        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, ct))
        {
            yield return ev;

            switch (ev)
            {
                case TransferCompleted tc:
                    success = true;
                    finalUrl = tc.FileUrl;
                    break;
                case AttemptFailed af:
                    failure = af.Exception;
                    break;
                case AttemptCancelled:
                    break;
            }
        }

        AttemptCompleted finalEvent = new(Success: success, ProxyId: proxy.Id, FileUrl: finalUrl);
        yield return finalEvent;
        this.AttemptCompleted?.Invoke(this, finalEvent);
        _ = failure; // reserved for richer reporting once all hosters wire AttemptFailed
    }
}
