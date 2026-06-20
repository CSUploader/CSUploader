// <copyright file="AttemptRunner.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Outer pipeline orchestrator. One <c>RunAsync</c> call = one upload attempt (with bounded,
/// universally-safe retries on body-incomplete transport faults).
/// Picks proxy → builds handler → invokes hoster pipeline → emits <see cref="AttemptCompleted"/>.
/// </summary>
public sealed class AttemptRunner(IFileHosterRegistry registry, IProxySource proxySource, IHttpHandlerFactory handlerFactory)
{
    /// <summary>
    /// Total number of times the whole hoster pipeline may run for a single upload. A retry
    /// re-invokes the entire <see cref="IFileHosterPipeline.RunAsync"/> (fresh discovery +
    /// re-send), which is only safe when the previous attempt's body never finished sending
    /// — see <see cref="UploadBodyTransferException"/>.
    /// </summary>
    private const int MaxUploadAttempts = 3;

    /// <summary>
    /// Raised after every attempt. <see cref="ProxyManager"/> subscribes here to update
    /// connectivity icons; the old <c>PackageManager.OnFileStateChanged</c> reach-through
    /// to <c>ProxyManager.Current</c> is replaced by this subscription.
    /// </summary>
    public event EventHandler<AttemptCompleted>? AttemptCompleted;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptInputs inputs, [EnumeratorCancellation] CancellationToken ct)
    {
        // Captcha-gated hosters pin a proxy to the credentials at sign-in time so every
        // request through the cookie's lifetime shares the issuing IP (XFileSharing binds
        // session cookies to the issuing IP). Honour the pin when set; when the pinned
        // proxy is gone (disabled or deleted in Connection Manager), fall back to the
        // rotation so the pipeline can recover by triggering a fresh sign-in through the
        // newly-picked proxy — the pipeline detects the proxy-id mismatch against the
        // stale pin and invalidates its cached cookie, popping the WebView again.
        ProxyChoice? proxy;
        if (inputs.Credentials.PinnedProxyId is int pinnedId)
        {
            proxy = proxySource.GetById(pinnedId)
                ?? proxySource.Next();

            // If both lookups failed, we drop through to the null-check below which fails
            // fast with the standard "Use Proxies on but no usable proxy" reason.
        }
        else
        {
            proxy = proxySource.Next();
        }

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

        // Bounded retry loop. The whole hoster pipeline is re-run (a fresh attempt) ONLY when
        // the previous attempt propagated a body-incomplete transport fault — the connection
        // aborted mid-send, so the server committed nothing and re-sending cannot double-create.
        // A server verdict (yielded AttemptFailed/AttemptCancelled) or success is terminal.
        // `yield return` can't live inside try/catch, so we pump the pipeline's events through a
        // channel on a background Task that captures any thrown transport fault.
        for (int attempt = 1; attempt <= MaxUploadAttempts; attempt++)
        {
            Channel<UploadEvent> channel = Channel.CreateUnbounded<UploadEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            Exception? fault = null;

            Task pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (UploadEvent ev in pipeline.RunAsync(ctx, ct))
                    {
                        channel.Writer.TryWrite(ev);
                    }
                }
                catch (Exception ex)
                {
                    fault = ex;
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            await foreach (UploadEvent ev in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return ev;
                if (ev is TransferCompleted tc)
                {
                    success = true;
                    finalUrl = tc.FileUrl;
                }
            }

            await pump; // ensure `fault` is published (and surface any pump-internal failure).

            if (fault is null)
            {
                // Terminal: success, a server-verdict AttemptFailed, or AttemptCancelled.
                break;
            }

            // Only OUR runner token counts as a user/scheduler cancellation. A bare OCE
            // carrying an unrelated or None token (an internal Task.Delay/timeout, or a
            // library's own linked token) is a FAULT, not a user-cancel — let it fall through
            // to the retryable check so it ends as Failed, not silently Cancelled.
            if ((fault is OperationCanceledException oce && oce.CancellationToken == ct)
                || ct.IsCancellationRequested)
            {
                // Genuine user/scheduler cancellation — never retry, and surface it as a
                // cancellation so the scheduler marks the row Cancelled (not Failed). The
                // final AttemptCompleted event is intentionally NOT emitted on this path
                // (matching prior behavior).
                throw new OperationCanceledException(ct);
            }

            bool retryable = UploadBodyTransferException.IsInChain(fault);
            if (retryable && attempt < MaxUploadAttempts)
            {
                if (await DelayBeforeRetryAsync(attempt, ct))
                {
                    // Cancelled during the back-off — treat as a genuine cancellation.
                    throw new OperationCanceledException(ct);
                }

                continue; // re-run the whole attempt; the pool opens a fresh connection.
            }

            // Non-retryable transport fault, or retries exhausted → terminal Failed (not
            // Cancelled). Yielding AttemptFailed (no thrown exception) lets the scheduler
            // mark the row Failed.
            string reason = retryable
                ? $"Upload failed after {MaxUploadAttempts} attempts (last transport error: {fault.Message})"
                : fault.Message;
            yield return new AttemptFailed(reason, fault);
            success = false;
            break;
        }

        AttemptCompleted finalEvent = new(Success: success, ProxyId: proxy.Id, FileUrl: finalUrl);
        yield return finalEvent;
        this.AttemptCompleted?.Invoke(this, finalEvent);
    }

    /// <summary>
    /// Backs off before re-running the pipeline (1s, 2s, …, keyed off the just-completed
    /// attempt number), cancellable. Returns <see langword="true"/> if the wait was cancelled
    /// (caller should abort as a cancellation), <see langword="false"/> to proceed with the retry.
    /// </summary>
    private static async Task<bool> DelayBeforeRetryAsync(int completedAttempt, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(completedAttempt), ct);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
