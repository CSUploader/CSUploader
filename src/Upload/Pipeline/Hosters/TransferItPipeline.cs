// <copyright file="TransferItPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// transfer.it — a frontend over MEGA's storage, so uploads are MEGA's end-to-end-encrypted protocol
/// (no transfer.it account; an anonymous ephemeral MEGA session per upload). Per file: create an
/// ephemeral session (<c>up</c>/<c>us</c>), create a transfer container (<c>xn</c>), pick a WebSocket
/// upload pool (<c>usc</c>), AES-CTR-encrypt-and-upload the bytes over the binary WS protocol, finalise
/// the file node (<c>xp</c>) with the condensed-MAC file key + encrypted filename, and close the
/// transfer (<c>xc</c>). The share link is <c>https://transfer.it/t/&lt;xh&gt;</c> — the keys live
/// server-side, so no <c>#key</c> fragment. Crypto + API + WS live in the <c>Mega/</c> helpers (each
/// verified against the transfer-it-cli reference's known-answer vectors).
/// </summary>
public sealed class TransferItPipeline : IFileHosterPipeline
{
    private readonly Func<AttemptContext, MegaApi>? _apiFactory;
    private readonly Func<MegaUploadPool, AttemptContext, uint[], Action<long, long>, CancellationToken, Task<(byte[] Token, List<uint[]> Macs)>>? _uploadFunc;

    public TransferItPipeline()
    {
    }

    /// <summary>Test ctor — substitutes the <see cref="MegaApi"/> and the WebSocket upload so the
    /// orchestration (event sequence, share URL, error handling) runs without the live MEGA backend.
    /// The WebSocket transfer itself is covered by <c>MegaWebSocketFramingTests</c> + the live test.</summary>
    internal TransferItPipeline(
        Func<AttemptContext, MegaApi> apiFactory,
        Func<MegaUploadPool, AttemptContext, uint[], Action<long, long>, CancellationToken, Task<(byte[] Token, List<uint[]> Macs)>> uploadFunc)
    {
        _apiFactory = apiFactory;
        _uploadFunc = uploadFunc;
    }

    public string Name => "Transfer.it";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => null; // MEGA enforces; transfer.it's anonymous tier is generous.

    public int? MaxFilesPerPackage => null;

    /// <summary>No transfer.it account — every upload spins up its own anonymous ephemeral MEGA
    /// session, so the wizard offers it as the built-in "Anonymous" option.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;
        MegaApi api = _apiFactory is not null
            ? _apiFactory(ctx)
            : new MegaApi((url, body, c) => ctx.Handler.PostJsonAsync(url, body, null, c));

        // === Phase 1: ephemeral session + transfer container + upload pool ===
        string xh = string.Empty;
        string rootHandle = string.Empty;
        MegaUploadPool pool = default;
        string? setupError = null;
        try
        {
            await api.CreateEphemeralSessionAsync(ctx.Cancellation);
            (xh, rootHandle, _) = await api.CreateTransferAsync(ctx.FileName, ctx.Cancellation);
            IReadOnlyList<MegaUploadPool> pools = await api.UploadPoolsAsync(ctx.Cancellation);
            pool = MegaApi.PickPool(pools, ctx.FileSize);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            setupError = "transfer.it setup failed: " + ex.Message;
        }

        if (setupError is not null)
        {
            yield return new AttemptFailed(setupError, null);
            yield break;
        }

        // === Phase 2: encrypt + upload the bytes over the MEGA WebSocket ===
        yield return new TransferStarted(ctx.FileSize);

        uint[] ulKey = MegaCrypto.RandA32(6);
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        var stopwatch = Stopwatch.StartNew();
        void Progress(long sent, long total)
        {
            double speed = sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progressChannel.Writer.TryWrite(new TransferProgress(sent, total, speed));
        }

        Task<(byte[] Token, List<uint[]> Macs)> uploadTask = _uploadFunc is not null
            ? _uploadFunc(pool, ctx, ulKey, Progress, ctx.Cancellation)
            : MegaWebSocketUploader.UploadAsync(pool.Host, pool.Uri, ctx.FilePath, ulKey, fileno: 1, ctx.FileSize, Progress, ctx.Cancellation);

        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent ev in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return ev;
        }

        // A failed WS upload created no file node (xp runs below), so re-running the whole pipeline is
        // safe — the exception propagates to the shared retry layer, which makes a fresh transfer.
        (byte[] token, List<uint[]> macs) = await uploadTask;

        // === Phase 3: finalise the file node + close the transfer ===
        string? finaliseError = null;
        string? shareUrl = null;
        try
        {
            await api.FinaliseFileAsync(rootHandle, token, ulKey, macs, ctx.FileName, ctx.Cancellation);
            await api.CloseTransferAsync(xh, ctx.Cancellation);
            shareUrl = $"{MegaApi.ShareBase}/t/{xh}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            finaliseError = "transfer.it finalise failed: " + ex.Message;
        }

        if (finaliseError is not null)
        {
            yield return new AttemptFailed(finaliseError, null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl!);
    }

    /// <summary>transfer.it has no accounts in this app — uploads use the built-in Anonymous option.</summary>
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, Lib.Net.Http.HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = username;
        _ = password;
        _ = apiKey;
        _ = handler;
        _ = proxy;
        _ = ct;
        return Task.FromResult(new AccountCheckResult(
            false,
            AccountType.Free,
            "transfer.it has no account sign-in — upload with the built-in Anonymous option in the wizard."));
    }
}
