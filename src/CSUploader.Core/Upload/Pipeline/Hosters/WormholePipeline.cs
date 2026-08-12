// <copyright file="WormholePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline.Hosters.Wormhole;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// wormhole.app upload pipeline — anonymous, end-to-end-encrypted (WebTorrent + RFC 8188 + Backblaze B2).
/// The whole file is encrypted client-side; the share link is <c>https://wormhole.app/&lt;id&gt;#&lt;key&gt;</c>
/// where the key lives in the URL fragment and never reaches the server. v1 = single file, cloud path
/// (the WebSocket P2P relay is skipped — the file persists on B2 for the room lifetime). Per upload:
/// <list type="number">
///   <item>Mint a 16-byte main key + salt; <c>POST /api/room {readerToken, salt}</c> → room id + writerToken.</item>
///   <item>Stream-ece-encrypt the file to a temp file (<see cref="WormholeCrypto.EceEncryptStream"/>),
///   hashing the ciphertext into torrent pieces; build the <c>.torrent</c> over the ciphertext, encrypt it
///   (<see cref="WormholeCrypto.EncryptMeta"/>).</item>
///   <item><c>PATCH /api/room/&lt;id&gt; {infoHash, encryptedTorrentFile, multiFile:false, sizeMb}</c>.</item>
///   <item><c>POST .../b2/auth-upload {numTokens:N}</c> → N Backblaze upload URLs+tokens; PUT each
///   5,013,504-byte ciphertext blob (<c>&lt;id&gt;/&lt;i&gt;</c>, <c>X-Bz-Content-Sha1</c>).</item>
///   <item><c>POST .../b2/finish-upload {success:true}</c>; the link is
///   <c>https://wormhole.app/&lt;id&gt;#base64url(mainKey)</c>.</item>
/// </list>
/// A failed blob commits nothing (finish-upload is the only record-creating step), so a mid-send abort is
/// safe to retry the whole pipeline (a fresh room + key). No account.
/// </summary>
public sealed class WormholePipeline : IFileHosterPipeline
{
    private const string Api = "https://wormhole.app";

    /// <summary>Cloud-upload cap (the room's <c>maxCloudSize</c>); above it wormhole is P2P-only, which
    /// this v1 doesn't do.</summary>
    private const long MaxCloudSize = 5_500_000_000;

    /// <summary>How many Backblaze B2 upload URLs to request from <c>b2/auth-upload</c> at once. The server
    /// 500s on a large count (a 21-object file asking for 21 tokens fails; the real client asks for 5 and
    /// reuses each URL for several sequential objects — B2 upload URLs are reusable). We request
    /// min(objectCount, this) and cycle the returned tokens across all objects.</summary>
    private const int MaxUploadTokens = 5;

    /// <summary>How many times a single B2 blob PUT is retried on a TRANSIENT failure (5xx / 408 / 429, or a
    /// mid-send body-transfer abort) before giving up on it. Backblaze documents 500/503 as retryable.</summary>
    private const int MaxBlobRetries = 4;

    private readonly Func<HttpMethod, string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _sendJsonOverride;
    private readonly Func<string, byte[], IReadOnlyDictionary<string, string>, Action<long>, Task<HttpResponseSnapshot>>? _uploadBlobOverride;
    private readonly Func<int, byte[]>? _randBytesOverride;
    private readonly Func<int, TimeSpan>? _retryBackoffOverride;

    public WormholePipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON API calls, the B2 blob upload, the key/salt generation and (optionally)
    /// the retry backoff so the orchestration (event sequence, manifest, blob split, link, blob retry) runs
    /// deterministically without the network or real delays. The crypto/torrent are KAT-tested separately.</summary>
    internal WormholePipeline(
        Func<HttpMethod, string, string?, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> sendJson,
        Func<string, byte[], IReadOnlyDictionary<string, string>, Action<long>, HttpResponseSnapshot> uploadBlob,
        Func<int, byte[]> randBytes,
        Func<int, TimeSpan>? retryBackoff = null)
    {
        _sendJsonOverride = (m, u, j, h) => Task.FromResult(sendJson(m, u, j, h));
        _uploadBlobOverride = (u, b, h, p) => Task.FromResult(uploadBlob(u, b, h, p));
        _randBytesOverride = randBytes;
        _retryBackoffOverride = retryBackoff;
    }

    // Exponential backoff with jitter for a transient B2 blob failure: ~0.5s, 1s, 2s, 4s, capped at 8s.
    private TimeSpan RetryBackoff(int attempt)
        => _retryBackoffOverride?.Invoke(attempt)
           ?? TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 250));

    public string Name => "Wormhole";

    /// <summary>From its own FAQ (read 2026-08-12): "Files are permanently deleted from the server
    /// after 24 hours." This is a transfer service, not storage.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => FileRetention.AfterUpload(TimeSpan.FromHours(24));

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxCloudSize;

    /// <summary>No per-package cap. Each selected file uploads independently to its own wormhole link (one
    /// room per file — the runner calls <see cref="RunAsync"/> once per file), so a package can hold as many
    /// files as the user selects; they show as N package files, each with its own link, exactly like every
    /// other single-file hoster. "single file per link" is a link property, not a package limit.</summary>
    public int? MaxFilesPerPackage => null;

    /// <summary>wormhole.app needs no account — the wizard offers it as a built-in "Anonymous" option.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>wormhole.app has no login anywhere on the site, so the Add Account dialog leaves it out
    /// of its hoster list — there is nothing to add.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxCloudSize)
        {
            yield return new AttemptFailed(
                $"File exceeds wormhole.app's {ByteUnit.FromBytes(MaxCloudSize, ByteBase.Decimal).ToFriendlyString()} cloud-upload limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // The main key never leaves the client (it goes in the link fragment); only the derived readerToken
        // and the salt are sent. A fresh key+room per attempt is what makes a retry non-double-creating.
        byte[] mainKey = RandBytes(WormholeCrypto.KeyLength);
        byte[] salt = RandBytes(WormholeCrypto.SaltLength);
        byte[] readerToken = WormholeCrypto.DeriveReaderToken(mainKey, salt);

        // === Step 1: create the room ===
        string roomJson = JsonSerializer.Serialize(new
        {
            readerToken = WormholeCrypto.ToB64(readerToken),
            salt = WormholeCrypto.ToB64(salt),
        });

        (HttpResponseSnapshot? roomResp, string? roomReqErr) = await TrySendJson(ctx, HttpMethod.Post, Api + "/api/room", roomJson, JsonHeaders());
        if (roomResp is null)
        {
            yield return new AttemptFailed(roomReqErr!, null);
            yield break;
        }

        (string? roomId, string? writerToken, string? roomError) = ParseRoom(roomResp);
        if (roomId is null || writerToken is null)
        {
            yield return new AttemptFailed(roomError ?? "wormhole.app room create returned no id/token", null);
            yield break;
        }

        long ciphertextLength = WormholeCrypto.EncryptedSize(ctx.FileSize);
        long pieceLength = WormholeTorrent.B2BlockSize; // piece length == B2 object size; recipient fetches one blob per piece
        string tempPath = Path.Combine(Path.GetTempPath(), "wh-" + roomId + ".enc");

        // Everything after the temp file exists runs inside try/finally so the ciphertext is always cleaned
        // up. (yield is legal inside a try with only a finally.)
        string? failure = null;
        bool propagate = false;
        try
        {
            // === Step 2: encrypt the file → temp ciphertext, hashing torrent pieces in the same pass ===
            byte[] pieceHashes = [];
            byte[] headerSalt = RandBytes(WormholeCrypto.SaltLength);
            string? encError = null;
            try
            {
                await using FileStream plaintext = File.OpenRead(ctx.FilePath);
                await using FileStream cipher = File.Create(tempPath);
                (_, pieceHashes) = WormholeCrypto.EceEncryptStream(plaintext, ctx.FileSize, mainKey, headerSalt, pieceLength, cipher);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                encError = "wormhole.app encryption failed: " + ex.Message;
            }

            if (encError is not null)
            {
                yield return new AttemptFailed(encError, null);
                yield break;
            }

            // === Step 3: build + encrypt the .torrent, PATCH the manifest ===
            (byte[] torrent, byte[] infoHash) = WormholeTorrent.Build(ctx.FileName, pieceLength, pieceHashes, ciphertextLength);
            byte[] metaKey = WormholeCrypto.DeriveMetadataKey(mainKey, salt);
            string manifestJson = JsonSerializer.Serialize(new
            {
                infoHash = Convert.ToHexStringLower(infoHash),
                encryptedTorrentFile = Convert.ToBase64String(WormholeCrypto.EncryptMeta(torrent, metaKey)),
                multiFile = false,
                sizeMb = (int)Math.Round(ctx.FileSize / 1_000_000.0, MidpointRounding.AwayFromZero),
            });

            (HttpResponseSnapshot? manifestResp, string? manifestReqErr) =
                await TrySendJson(ctx, HttpMethod.Patch, $"{Api}/api/room/{roomId}", manifestJson, AuthHeaders(writerToken));
            if (manifestResp is null || manifestResp.StatusCode is < 200 or >= 300)
            {
                failure = manifestReqErr ?? $"wormhole.app manifest PATCH failed (HTTP {manifestResp?.StatusCode}): {Snippet(manifestResp?.Body)}";
            }
            else
            {
                // === Step 4: get B2 upload tokens, then stream the blobs up (one B2 object per piece) ===
                int blobCount = (int)WormholeTorrent.PieceCount(ciphertextLength, pieceLength);
                string authJson = JsonSerializer.Serialize(new { numTokens = Math.Min(blobCount, MaxUploadTokens) });
                (HttpResponseSnapshot? authResp, string? authReqErr) =
                    await TrySendJson(ctx, HttpMethod.Post, $"{Api}/api/room/{roomId}/b2/auth-upload", authJson, AuthHeaders(writerToken));

                List<(string Url, string Token)> tokens = [];
                if (authResp is null)
                {
                    failure = authReqErr;
                }
                else
                {
                    (tokens, string? authError) = ParseAuthUpload(authResp);
                    if (tokens.Count == 0)
                    {
                        failure = authError ?? "wormhole.app auth-upload returned no B2 tokens";
                    }
                }

                if (failure is null)
                {
                    yield return new TransferStarted(ciphertextLength);

                    var progressChannel = Channel.CreateUnbounded<UploadEvent>();
                    var stopwatch = Stopwatch.StartNew();
                    void Progress(long sent, long total) =>
                        progressChannel.Writer.TryWrite(new TransferProgress(sent, total, sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));

                    Task<(bool Ok, string? Error, bool Propagate)> blobTask =
                        UploadBlobsAsync(ctx, tempPath, roomId, ciphertextLength, pieceLength, tokens, Progress);
                    _ = blobTask.ContinueWith(
                        _ => progressChannel.Writer.Complete(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    await foreach (UploadEvent ev in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
                    {
                        yield return ev;
                    }

                    (bool ok, string? blobErr, bool blobPropagate) = await blobTask;
                    if (!ok)
                    {
                        failure = blobErr;
                        propagate = blobPropagate;
                    }
                    else
                    {
                        yield return new TransferProgress(ciphertextLength, ciphertextLength, ciphertextLength / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

                        // === Step 5: finish ===
                        (HttpResponseSnapshot? finishResp, string? finishReqErr) =
                            await TrySendJson(ctx, HttpMethod.Post, $"{Api}/api/room/{roomId}/b2/finish-upload", """{"success":true}""", AuthHeaders(writerToken));
                        if (finishResp is null || finishResp.StatusCode is < 200 or >= 300)
                        {
                            failure = finishReqErr ?? $"wormhole.app finish-upload failed (HTTP {finishResp?.StatusCode}): {Snippet(finishResp?.Body)}";
                        }
                    }
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // best-effort cleanup of the temp ciphertext
            }
        }

        // A retryable body-transfer fault from the blob upload must PROPAGATE (re-run against a fresh room);
        // any other failure is terminal for this attempt.
        if (propagate)
        {
            throw new Lib.Net.Http.UploadBodyTransferException(new IOException(failure ?? "wormhole.app blob upload aborted"));
        }

        if (failure is not null)
        {
            yield return new AttemptFailed(failure, null);
            yield break;
        }

        yield return new TransferCompleted($"{Api}/{roomId}#{WormholeCrypto.KeyToFragment(mainKey)}");
    }

    /// <summary>wormhole.app has no accounts — uploads use the built-in Anonymous option.</summary>
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
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
            "wormhole.app has no account sign-in — upload with the built-in Anonymous option in the wizard."));
    }

    /// <summary>Uploads the ciphertext temp file to Backblaze B2 as one object per torrent piece — object
    /// <c>&lt;roomId&gt;/&lt;i&gt;</c> holds piece <c>i</c>'s bytes (<paramref name="pieceLength"/> each, last
    /// short). The recipient fetches these blobs back by piece index, so the split MUST match the torrent's.
    /// Returns (ok, error, propagate): propagate=true means a retryable mid-send abort that should re-run the
    /// whole pipeline (finish-upload never ran, so nothing was committed).</summary>
    private async Task<(bool Ok, string? Error, bool Propagate)> UploadBlobsAsync(
        AttemptContext ctx, string cipherPath, string roomId, long ciphertextLength, long pieceLength, List<(string Url, string Token)> tokens, Action<long, long> progress)
    {
        int blobCount = (int)WormholeTorrent.PieceCount(ciphertextLength, pieceLength);
        long baseOffset = 0;

        await using FileStream? fs = _uploadBlobOverride is null ? File.OpenRead(cipherPath) : null;
        try
        {
            for (int i = 0; i < blobCount; i++)
            {
                int blobSize = WormholeTorrent.PieceSizeAt(ciphertextLength, pieceLength, i);
                byte[] blob = new byte[blobSize];
                if (fs is not null)
                {
                    fs.ReadExactly(blob, 0, blobSize);
                }

                string sha1 = Convert.ToHexStringLower(SHA1.HashData(blob));
                long blobBase = baseOffset;
                void OnBlobProgress(long sentInBlob) => progress(blobBase + sentInBlob, ciphertextLength);

                // Fewer tokens than objects (the server caps the pool): reuse each B2 upload URL for its share
                // of objects, round-robin (sequential reuse is allowed). On a TRANSIENT B2 failure — 5xx / 408 /
                // 429, or a mid-send body-transfer abort — retry the blob with exponential backoff, rotating to
                // the next upload URL in the pool (Backblaze documents 500/503 as retryable and advises a fresh
                // upload URL). The blob is buffered in memory, and nothing is committed until finish-upload, so a
                // retry just re-sends the same bytes to the deterministic object name.
                int slot = i % tokens.Count;
                int attempt = 0;
                while (true)
                {
                    (string url, string token) = tokens[slot];
                    Dictionary<string, string> headers = new(StringComparer.Ordinal)
                    {
                        ["Authorization"] = token,
                        ["X-Bz-File-Name"] = $"{roomId}/{i}",
                        ["X-Bz-Content-Sha1"] = sha1,
                    };

                    HttpResponseSnapshot? resp = null;
                    Exception? transportEx = null;
                    try
                    {
                        resp = await UploadBlob(ctx, url, blob, headers, OnBlobProgress);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        transportEx = ex;
                    }

                    if (resp is { StatusCode: >= 200 and < 300 })
                    {
                        break; // uploaded
                    }

                    bool bodyFault = transportEx is not null && Lib.Net.Http.UploadBodyTransferException.IsInChain(transportEx);
                    bool httpTransient = resp is not null && resp.StatusCode is 500 or 502 or 503 or 408 or 429;
                    if (!bodyFault && !httpTransient)
                    {
                        // Non-transient: a hard HTTP rejection or a non-body transport error — terminal.
                        return transportEx is not null
                            ? (false, "wormhole.app B2 upload failed: " + transportEx.Message, false)
                            : (false, $"wormhole.app B2 blob {i} rejected (HTTP {resp!.StatusCode}): {Snippet(resp.Body)}", false);
                    }

                    if (++attempt > MaxBlobRetries)
                    {
                        // Exhausted. A persistent body-transfer fault still PROPAGATES so the shared retry layer
                        // can re-run the whole pipeline against a fresh room (nothing was committed); a persistent
                        // HTTP rejection is terminal for this attempt.
                        return resp is not null
                            ? (false, $"wormhole.app B2 blob {i} still failing after {MaxBlobRetries} retries (HTTP {resp.StatusCode}): {Snippet(resp.Body)}", false)
                            : (false, $"wormhole.app B2 blob {i} upload aborted after {MaxBlobRetries} retries: {transportEx!.Message}", bodyFault);
                    }

                    await Task.Delay(RetryBackoff(attempt), ctx.Cancellation).ConfigureAwait(false);
                    slot = (slot + 1) % tokens.Count; // rotate to a different upload URL for the retry
                }

                baseOffset += blobSize;
            }

            return (true, null, false);
        }
        finally
        {
            // fs disposed by await using
        }
    }

    private async Task<HttpResponseSnapshot> UploadBlob(AttemptContext ctx, string url, byte[] blob, IReadOnlyDictionary<string, string> headers, Action<long> onBlobProgress)
    {
        if (_uploadBlobOverride is not null)
        {
            return await _uploadBlobOverride(url, blob, headers, onBlobProgress);
        }

        void OnProgress(object? _, OperationProgressEventArgs e) => onBlobProgress(e.BytesProcessed);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            return await ctx.Handler.UploadBytesAsync(HttpMethod.Post, url, blob, "application/octet-stream", headers, ctx.SpeedLimitProvider, ctx.Cancellation);
        }
        finally
        {
            ctx.Handler.UploadProgress -= OnProgress;
        }
    }

    private byte[] RandBytes(int n) => _randBytesOverride is not null ? _randBytesOverride(n) : RandomNumberGenerator.GetBytes(n);

    private async Task<(HttpResponseSnapshot? Response, string? Error)> TrySendJson(
        AttemptContext ctx, HttpMethod method, string url, string? json, IReadOnlyDictionary<string, string> headers)
    {
        try
        {
            HttpResponseSnapshot resp = _sendJsonOverride is not null
                ? await _sendJsonOverride(method, url, json, headers)
                : await ctx.Handler.SendJsonAsync(method, url, json, headers, ctx.Cancellation);
            return (resp, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"wormhole.app {method.Method} {new Uri(url).AbsolutePath} request failed: {ex.Message}");
        }
    }

    private static Dictionary<string, string> JsonHeaders() => new(StringComparer.Ordinal)
    {
        ["Accept"] = "application/json",
        ["Origin"] = Api,
        ["Referer"] = Api + "/",
    };

    private static Dictionary<string, string> AuthHeaders(string writerToken)
    {
        Dictionary<string, string> h = JsonHeaders();
        h["Authorization"] = "Bearer sync-v1 " + writerToken;
        return h;
    }

    private static (string? Id, string? WriterToken, string? Error) ParseRoom(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"wormhole.app room create failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            string? writer = root.TryGetProperty("writerToken", out JsonElement wt) ? wt.GetString() : null;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(writer))
            {
                return (id, writer, null);
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return (null, null, $"wormhole.app room create returned no id/writerToken: {Snippet(response.Body)}");
    }

    private static (List<(string Url, string Token)> Tokens, string? Error) ParseAuthUpload(HttpResponseSnapshot response)
    {
        List<(string, string)> tokens = [];
        if (response.StatusCode is < 200 or >= 300)
        {
            return (tokens, $"wormhole.app auth-upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in doc.RootElement.EnumerateArray())
                {
                    string? url = e.TryGetProperty("uploadUrl", out JsonElement u) ? u.GetString() : null;
                    string? token = e.TryGetProperty("authorizationToken", out JsonElement t) ? t.GetString() : null;
                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(token))
                    {
                        tokens.Add((url, token));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return (tokens, tokens.Count == 0 ? $"wormhole.app auth-upload returned no usable tokens: {Snippet(response.Body)}" : null);
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim().Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
