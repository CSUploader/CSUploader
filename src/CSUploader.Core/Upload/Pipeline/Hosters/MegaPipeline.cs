// <copyright file="MegaPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// mega.nz — account uploads into the user's Cloud Drive over MEGA's end-to-end-encrypted
/// protocol. Per file: password login (<c>us0</c>/<c>us</c> — PBKDF2 v2 or legacy v1 derivation +
/// RSA session recovery, see <see cref="MegaLoginCrypto"/>), fetch the Cloud Drive root
/// (<c>f</c>), pick a WebSocket upload pool (<c>usc</c>), AES-CTR-encrypt-and-upload the bytes
/// over the same binary WS protocol transfer.it uses, attach the node (<c>p</c>, key wrapped with
/// the account master key) and export it (<c>l</c>). The share link is
/// <c>https://mega.nz/file/&lt;ph&gt;#&lt;fileKey&gt;</c> — the key rides the fragment and never
/// reaches the server. Requires an account (2FA-protected accounts are not supported); wire
/// shapes reconciled against a live mega.nz web capture.
/// </summary>
public sealed class MegaPipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    private readonly Func<AttemptContext, MegaApi>? _apiFactory;
    private readonly Func<MegaUploadPool, AttemptContext, uint[], Action<long, long>, CancellationToken, Task<(byte[] Token, List<uint[]> Macs)>>? _uploadFunc;
    private readonly Func<MegaApi>? _accountApiFactory;

    public MegaPipeline()
    {
    }

    /// <summary>Test ctor — substitutes the <see cref="MegaApi"/> and the WebSocket upload so the
    /// orchestration (event sequence, share URL, error handling) runs without the live MEGA
    /// backend. The WS transfer itself is covered by <c>MegaWebSocketFramingTests</c>.
    /// <paramref name="accountApiFactory"/> stubs the non-upload (login + quota) path used by
    /// <see cref="CheckAccountAsync"/> and <see cref="RefreshStorageAsync"/>.</summary>
    internal MegaPipeline(
        Func<AttemptContext, MegaApi> apiFactory,
        Func<MegaUploadPool, AttemptContext, uint[], Action<long, long>, CancellationToken, Task<(byte[] Token, List<uint[]> Macs)>> uploadFunc,
        Func<MegaApi>? accountApiFactory = null)
    {
        _apiFactory = apiFactory;
        _uploadFunc = uploadFunc;
        _accountApiFactory = accountApiFactory;
    }

    public string Name => "MEGA";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No per-file cap — the account's storage quota is the limit, and the server
    /// rejects an over-quota upload (EOVERQUOTA) with the bytes unspent.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>mega.nz uploads land in an account's Cloud Drive — no anonymous path (that's
    /// what Transfer.it is for).</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;
        MegaApi api = MakeApi(ctx);

        // === Phase 1: login + Cloud Drive root + upload pool ===
        byte[] masterKey = [];
        string rootHandle = string.Empty;
        MegaUploadPool pool = default;
        string? setupError = null;
        try
        {
            masterKey = await api.LoginAsync(ctx.Credentials.Username ?? string.Empty, ctx.Credentials.Password ?? string.Empty, ctx.Cancellation);
            rootHandle = await api.FetchCloudRootAsync(ctx.Cancellation);
            IReadOnlyList<MegaUploadPool> pools = await api.UploadPoolsAsync(ctx.Cancellation);
            pool = MegaApi.PickPool(pools, ctx.FileSize);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            setupError = "MEGA setup failed: " + ex.Message;
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

        // A failed WS upload created no file node (p runs only below), so re-running is safe and
        // never double-creates. Wrap the fault as a body-transfer abort so the shared retry layer
        // (AttemptRunner) re-runs the whole pipeline against a fresh session.
        byte[] token;
        List<uint[]> macs;
        try
        {
            (token, macs) = await uploadTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Lib.Net.Http.UploadBodyTransferException(ex);
        }

        // Land the bar on 100% even if COMPLETE raced ahead of the last chunk's ack.
        yield return new TransferProgress(ctx.FileSize, ctx.FileSize, ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        // === Phase 3: attach the node to the Cloud Drive + export the public link ===
        string? finaliseError = null;
        string? shareUrl = null;
        try
        {
            (string nodeHandle, uint[] fileKey) = await api.PutFileNodeAsync(rootHandle, token, ulKey, macs, ctx.FileName, masterKey, ctx.Cancellation);
            string publicHandle = await api.ExportNodeAsync(nodeHandle, ctx.Cancellation);
            shareUrl = $"{MegaApi.MegaNzShareBase}/file/{publicHandle}#{MegaCrypto.A32ToB64(fileKey)}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            finaliseError = "MEGA finalise failed: " + ex.Message;
        }

        if (finaliseError is not null)
        {
            yield return new AttemptFailed(finaliseError, null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl!);
    }

    /// <summary>Validates the account by logging in and reads the storage numbers (<c>uq</c>).
    /// 2FA-protected accounts come back invalid with a pointed message (−26).</summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, Lib.Net.Http.HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;
        MegaApi api = MakeAccountApi(handler);

        try
        {
            await api.LoginAsync(username, password, ct);
            (long used, long total, bool paid) = await api.QuotaAsync(ct);
            return new AccountCheckResult(
                true,
                paid ? AccountType.Premium : AccountType.Free,
                StorageUsedBytes: used,
                StorageQuotaBytes: total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MegaApiException ex) when (ex.Code == -26)
        {
            return new AccountCheckResult(false, AccountType.Free, "This MEGA account has two-factor authentication enabled — 2FA logins aren't supported. Disable 2FA or use a different account.");
        }
        catch (MegaApiException ex) when (ex.Code is -9 or -2 or -16)
        {
            return new AccountCheckResult(false, AccountType.Free, $"MEGA login failed — check the email and password ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "MEGA login failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard's Summary page: a fresh login + <c>uq</c> read
    /// with the stored email/password (MEGA's login is a plain credential ceremony — no captcha). This
    /// matters here because the free tier's 10 GiB quota is tight, so the capacity fit needs live
    /// numbers. Returns null on any failure so the caller keeps the last-known snapshot.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(Dal.FileHosterLoginDto credentials, Lib.Net.Http.HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // the handler already routes through the chosen proxy.
        MegaApi api = MakeAccountApi(handler);
        try
        {
            await api.LoginAsync(credentials.Username ?? string.Empty, credentials.Password ?? string.Empty, ct);
            (long used, long total, _) = await api.QuotaAsync(ct);
            return new StorageUsage(used, total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Bad/expired creds, rate-limit, transport, or an unexpected shape — keep the snapshot.
            return null;
        }
    }

    private MegaApi MakeApi(AttemptContext ctx)
        => _apiFactory is not null
            ? _apiFactory(ctx)
            : new MegaApi((url, body, c) => ctx.Handler.PostJsonAsync(url, body, null, c), MegaApi.MegaNzApiBase);

    private MegaApi MakeAccountApi(Lib.Net.Http.HttpHandler handler)
        => _accountApiFactory is not null
            ? _accountApiFactory()
            : new MegaApi((url, body, c) => handler.PostJsonAsync(url, body, null, c), MegaApi.MegaNzApiBase);
}
