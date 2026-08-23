// <copyright file="StorageToPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// storage.to upload pipeline — anonymous (no-login) uploads. Verified against a live capture
/// 2026-06-29. storage.to is a Laravel front end that hands the bytes straight to Cloudflare R2 via a
/// presigned URL, so an upload is three steps:
/// <list type="number">
///   <item><b>Bootstrap.</b> GET <c>https://storage.to/</c> for the CSRF token
///   (<c>&lt;meta name="csrf-token" content="…"&gt;</c>) and the session cookies
///   (<c>XSRF-TOKEN</c>, <c>storageto-session</c>). The runner's handler is built without
///   <c>UseCookies</c>, so the cookies are forwarded by hand (same as GigaPeta).</item>
///   <item><b>Init.</b> POST <c>/api/upload/init-batch</c> (JSON
///   <c>{"files":[{filename,content_type,size}],"as_temp":false}</c>) with the
///   <c>X-CSRF-TOKEN</c> header + cookies → a per-file <c>upload_url</c> (an R2 presigned PUT) and an
///   <c>r2_key</c>. The response rotates <c>storageto-session</c>, so the jar is refreshed from it.</item>
///   <item><b>Transfer.</b> PUT the raw bytes to <c>upload_url</c> — a direct Cloudflare R2 target
///   whose query-string signature covers only <c>host</c>, so no cookies/auth, just the bytes + a
///   <c>Content-Type</c>. Success is <c>200</c> with an <c>ETag</c>.</item>
///   <item><b>Confirm.</b> POST <c>/api/upload/confirm-batch</c> (JSON
///   <c>{"files":[{filename,size,content_type,r2_key}],"collection_id":null,"upload_speed":N,"as_temp":false}</c>)
///   → <c>results.0.file.url</c> is the shareable link (<c>https://storage.to/&lt;id&gt;</c>).</item>
/// </list>
/// No hashing. Anonymous cap is 25 GB and files auto-delete after 3 days (the confirm step's file
/// record is what actually creates the share — a failed PUT before it leaves nothing behind, which is
/// why a mid-send abort is safe to retry). The account path is intentionally not wired up yet
/// (see <see cref="CheckAccountAsync"/>); uploads use the wizard's built-in Anonymous option.
/// <para>
/// DISABLED 2026-08-16 — the class is retained (DI registration + the FileHosterClient registry
/// entry are commented out; the smoke test asserts it is absent from the registry) so a re-enable
/// is low-churn.
/// </para>
/// <para>
/// Why: storage.to's whole zone moved behind a Cloudflare <b>managed</b> challenge
/// (<c>cType:'managed'</c>). The bootstrap step above — the <c>GET https://storage.to/</c> that
/// scrapes the CSRF token and seeds the session cookies — now gets the "Just a moment…"
/// interstitial (HTTP 403, <c>Cf-Mitigated: challenge</c>) instead of the homepage, so the flow
/// can't even reach init-batch. Everything below the bootstrap depends on that token+session, and
/// init/confirm hit storage.to itself (behind the same zone), so there is no API entry point that
/// sidesteps it. Verified working 2026-06-29; the challenge appeared since.
/// </para>
/// <para>
/// What can't fix it: an API-only call — the API needs the challenged homepage's CSRF/session, and
/// its own calls are behind the same challenge. And the app's cf_clearance path (solve in the
/// embedded browser, pin the UA, forward the clearance) was implemented + tested for TakeFile and
/// rejected regardless: a <i>managed</i> challenge also validates the browser TLS fingerprint, which
/// a .NET <c>HttpClient</c> can't reproduce, so a valid clearance + matching UA + IP still fails.
/// Same wall as TakeFile/ExtMatrix/Hotlink/FlashBit.
/// </para>
/// <para>
/// Re-enable checklist (only after confirming storage.to no longer serves a managed challenge to
/// non-browser clients): (1) un-comment the DI registration in ServiceRegistration.cs; (2) un-comment
/// the FileHosterClient registry entry; (3) flip the StorageToPipelineUploadTests sentinel back to
/// asserting the registry contains it. The wizard's Anonymous list is built from the DI-registered
/// pipelines, so (1) restores it there automatically.
/// </para>
/// </summary>
public sealed partial class StorageToPipeline : IFileHosterPipeline
{
    private const string Host = "https://storage.to";
    private const string HomeUrl = Host + "/";
    private const string InitBatchUrl = Host + "/api/upload/init-batch";
    private const string ConfirmBatchUrl = Host + "/api/upload/confirm-batch";
    private const string CompleteMultipartUrl = Host + "/api/upload/complete-multipart";

    /// <summary>Anonymous per-file cap — 25 GB, the figure storage.to advertises for no-signup
    /// uploads (storage.to/send-large-files). The server is the real gate (it can reject at
    /// <c>init-batch</c>, before any bytes go up), but failing fast here skips a guaranteed-doomed
    /// round-trip on an obviously-too-big file. Decimal GB to match the marketing number.</summary>
    private const long AnonymousMaxFileSizeBytes = 25L * 1000 * 1000 * 1000;

    // <meta name="csrf-token" content="..."> on the homepage — tolerant of attribute order.
    private static readonly Regex _csrfTokenRegex = CsrfTokenRegex();

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, string, Action<long, long>, SpeedBudget?, Task<HttpResponseSnapshot>>? _putOverride;
    private readonly Func<string, int, HttpResponseSnapshot>? _putPartOverride;

    public StorageToPipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET, the JSON API POSTs, the single R2 PUT and (optionally)
    /// the multipart part PUTs from canned responses so the bootstrap/init/transfer/complete/confirm
    /// orchestration runs without the network. The single-PUT override is handed the progress callback so a
    /// test can exercise the TransferProgress bridge; the part override receives (url, partNumber).</summary>
    internal StorageToPipeline(
        Func<string, HttpResponseSnapshot> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postJsonOverride,
        Func<string, string, string, Action<long, long>, HttpResponseSnapshot> putOverride,
        Func<string, int, HttpResponseSnapshot>? putPartOverride = null)
    {
        _getOverride = (url, _) => Task.FromResult(getOverride(url));
        _postJsonOverride = (url, body, headers) => Task.FromResult(postJsonOverride(url, body, headers));
        _putOverride = (filePath, url, contentType, progress, _) => Task.FromResult(putOverride(filePath, url, contentType, progress));
        _putPartOverride = putPartOverride;
    }

    public string Name => "Storage.to";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

    /// <summary>3 days for the anonymous route, which is the only one wired up here. The account path
    /// is deliberately unimplemented, so its retention is unknown rather than assumed to match.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterUpload(3) : FileRetention.Unspecified;

    public int? MaxFilesPerPackage => null;

    /// <summary>storage.to accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: anonymous per-file size cap ===
        if (ctx.FileSize > AnonymousMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds storage.to's anonymous {ByteUnit.FromBytes(AnonymousMaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        string contentType = MimeTypeGuesser.Guess(ctx.FilePath);

        // === Step 1: bootstrap — CSRF token + session cookies from the homepage ===
        (string? csrfToken, Dictionary<string, string> cookieJar, string? bootError) = await BootstrapAsync(ctx);
        if (csrfToken is null)
        {
            yield return new AttemptFailed(bootError ?? "storage.to bootstrap failed", null);
            yield break;
        }

        // === Step 2: init-batch — exchange file metadata for a presigned R2 PUT URL ===
        string initBody = JsonSerializer.Serialize(new
        {
            files = new[] { new { filename = ctx.FileName, content_type = contentType, size = ctx.FileSize } },
            as_temp = false,
        });

        HttpResponseSnapshot? initResponse = null;
        string? initRequestError = null;
        try
        {
            initResponse = await PostJsonAsync(ctx, InitBatchUrl, initBody, BuildApiHeaders(csrfToken, cookieJar));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // yield can't live in a catch inside an iterator — stash the message and surface it below.
            initRequestError = "storage.to init-batch request failed: " + ex.Message;
        }

        if (initResponse is null)
        {
            yield return new AttemptFailed(initRequestError ?? "storage.to init-batch request failed", null);
            yield break;
        }

        MergeSetCookies(cookieJar, initResponse.SetCookies);
        (InitBatch? init, string? initError) = ParseInitBatch(initResponse);
        if (init is null)
        {
            yield return new AttemptFailed(initError ?? "storage.to init-batch returned no upload URL", null);
            yield break;
        }

        // === Step 3: transfer the bytes to Cloudflare R2 — either a single presigned PUT, or (when
        // storage.to splits a large file into a multipart upload) N part PUTs + a complete-multipart call. ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        var stopwatch = Stopwatch.StartNew();
        void Progress(long sent, long total)
        {
            double speed = sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progressChannel.Writer.TryWrite(new TransferProgress(sent, total, speed));
        }

        Task<(bool Ok, string? Error)> transferTask = init.IsMultipart
            ? UploadMultipartAsync(ctx, init, csrfToken, cookieJar, Progress)
            : UploadSingleAsync(ctx, init.UploadUrl!, contentType, Progress);

        _ = transferTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        // A thrown transport fault (mid-send/connect abort) propagates RAW out of the await below:
        // UploadPutAsync/PutChunkAsync reclassify it as a safe-to-retry UploadBodyTransferException and the
        // shared retry layer (AttemptRunner) re-runs the whole pipeline against a FRESH init-batch — nothing is
        // committed until confirm-batch, so no double-create. A server verdict (non-2xx) is terminal.
        (bool transferOk, string? transferError) = await transferTask;
        if (!transferOk)
        {
            yield return new AttemptFailed(transferError ?? "storage.to upload failed", null);
            yield break;
        }

        // Land the bar on 100% (the PUTs report progress off the request-body write, which can finish a beat
        // before the response returns).
        yield return new TransferProgress(ctx.FileSize, ctx.FileSize, ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        // === Step 4: confirm-batch — register the object and get the share link (identical for both paths) ===
        long uploadSpeed = (long)(ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
        (string? shareUrl, string? confirmError) = await ConfirmBatchAsync(ctx, csrfToken, cookieJar, contentType, init.R2Key, ctx.FileSize, uploadSpeed);
        if (shareUrl is null)
        {
            yield return new AttemptFailed(confirmError ?? "storage.to confirm-batch failed", null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl);
    }

    /// <summary>Single presigned R2 PUT (small files). Returns (ok, error) for a server verdict; a mid-send/
    /// connect transport fault is deliberately left to THROW so it propagates raw out of RunAsync to the shared
    /// retry layer (the reclassified UploadBodyTransferException re-runs the whole pipeline from a fresh
    /// init-batch). OperationCanceledException likewise propagates.</summary>
    private async Task<(bool Ok, string? Error)> UploadSingleAsync(
        AttemptContext ctx, string uploadUrl, string contentType, Action<long, long> progress)
    {
        HttpResponseSnapshot putResponse = await PutAsync(ctx, uploadUrl, contentType, progress, ctx.SpeedBudget);
        return putResponse.StatusCode is >= 200 and < 300
            ? (true, null)
            : (false, $"storage.to R2 upload failed (HTTP {putResponse.StatusCode}): {Snippet(putResponse.Body)}");
    }

    /// <summary>Multipart R2 upload (large files): PUT each 32-MiB part to its presigned URL, collect the
    /// per-part ETags, then POST complete-multipart so storage.to finalises the R2 upload server-side. Returns
    /// (ok, error) for a server verdict; like the single path, a part-PUT transport fault is left to THROW so it
    /// propagates raw to the retry layer (nothing is committed until complete-multipart + confirm-batch).</summary>
    private async Task<(bool Ok, string? Error)> UploadMultipartAsync(
        AttemptContext ctx, InitBatch init, string csrfToken, Dictionary<string, string> cookieJar, Action<long, long> progress)
    {
        long total = ctx.FileSize;
        DateTime started = DateTime.Now;
        (int PartNumber, string ETag)[] parts = new (int, string)[init.PartUrls.Count];

        void OnProgress(object? _, OperationProgressEventArgs e) => progress(e.BytesProcessed, e.Size);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            await using FileStream? fs = _putPartOverride is null ? new FileStream(ctx.FilePath, FileMode.Open, FileAccess.Read) : null;
            for (int i = 0; i < init.PartUrls.Count; i++)
            {
                int partNumber = i + 1;
                long basePos = (long)i * init.PartSize;
                long len = Math.Min(init.PartSize, total - basePos);

                // A part-PUT transport fault (or cancellation) is left to THROW — like the single path it
                // propagates raw to the retry layer, which re-runs from a fresh init-batch. Safe because
                // nothing is committed until complete-multipart + confirm-batch below.
                HttpResponseSnapshot resp = _putPartOverride is not null
                    ? _putPartOverride(init.PartUrls[i], partNumber)
                    : await ctx.Handler.PutChunkAsync(
                        init.PartUrls[i], new ChunkSliceStream(fs!, len), len, basePos, total, started,
                        headers: null, ctx.SpeedBudget, ctx.Cancellation);

                if (resp.StatusCode is < 200 or >= 300)
                {
                    return (false, $"storage.to R2 part {partNumber} rejected (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
                }

                if (string.IsNullOrEmpty(resp.ETag))
                {
                    return (false, $"storage.to R2 part {partNumber} returned no ETag");
                }

                parts[i] = (partNumber, resp.ETag);
            }
        }
        finally
        {
            ctx.Handler.UploadProgress -= OnProgress;
        }

        // complete-multipart: hand storage.to the part ETags so it finalises the R2 multipart server-side.
        string completeBody = JsonSerializer.Serialize(new
        {
            upload_id = init.MultipartUploadId,
            parts = parts.Select(p => new { partNumber = p.PartNumber, etag = p.ETag }),
        });

        HttpResponseSnapshot completeResp;
        try
        {
            completeResp = await PostJsonAsync(ctx, CompleteMultipartUrl, completeBody, BuildApiHeaders(csrfToken, cookieJar));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "storage.to complete-multipart request failed: " + ex.Message);
        }

        MergeSetCookies(cookieJar, completeResp.SetCookies);
        return completeResp.StatusCode is >= 200 and < 300 && IsSuccessEnvelope(completeResp.Body)
            ? (true, null)
            : (false, $"storage.to complete-multipart failed (HTTP {completeResp.StatusCode}): {Snippet(completeResp.Body)}");
    }

    /// <summary>confirm-batch — registers the uploaded object and returns the share link. Identical for the
    /// single and multipart paths.</summary>
    private async Task<(string? ShareUrl, string? Error)> ConfirmBatchAsync(
        AttemptContext ctx, string csrfToken, Dictionary<string, string> cookieJar, string contentType, string r2Key, long fileSize, long uploadSpeed)
    {
        string confirmBody = JsonSerializer.Serialize(new
        {
            files = new[] { new { filename = ctx.FileName, size = fileSize, content_type = contentType, r2_key = r2Key } },
            collection_id = (string?)null,
            upload_speed = uploadSpeed,
            as_temp = false,
        });

        HttpResponseSnapshot confirmResponse;
        try
        {
            confirmResponse = await PostJsonAsync(ctx, ConfirmBatchUrl, confirmBody, BuildApiHeaders(csrfToken, cookieJar));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "storage.to confirm-batch request failed: " + ex.Message);
        }

        MergeSetCookies(cookieJar, confirmResponse.SetCookies);
        return ParseConfirmBatch(confirmResponse);
    }

    /// <summary>True when a gofile/storage.to-style envelope is <c>{"success":true,…}</c>.</summary>
    private static bool IsSuccessEnvelope(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out JsonElement s)
                && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// storage.to login isn't wired up yet — uploads use the anonymous path. Surface a clear message
    /// rather than a silent failure if someone tries to add a storage.to account in Settings.
    /// </summary>
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
            "storage.to login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>GETs the homepage, scrapes the <c>csrf-token</c> meta tag, and seeds a cookie jar from
    /// its <c>Set-Cookie</c>s (the API POSTs need both the token and the session cookie).</summary>
    private async Task<(string? CsrfToken, Dictionary<string, string> CookieJar, string? Error)> BootstrapAsync(AttemptContext ctx)
    {
        Dictionary<string, string> jar = new(StringComparer.Ordinal);

        HttpResponseSnapshot snap;
        try
        {
            snap = await GetAsync(ctx, HomeUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, jar, "storage.to homepage fetch failed: " + ex.Message);
        }

        MergeSetCookies(jar, snap.SetCookies);

        Match m = _csrfTokenRegex.Match(snap.Body);
        if (!m.Success)
        {
            return (null, jar, $"storage.to homepage did not contain a csrf-token (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        string token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return (token, jar, null);
    }

    /// <summary>The parsed init-batch outcome: a single presigned R2 PUT (<see cref="UploadUrl"/>) for small
    /// files, or a multipart upload (<see cref="MultipartUploadId"/> + <see cref="PartSize"/> + per-part
    /// presigned URLs in <see cref="PartUrls"/>, 0-based so index i is part number i+1) for large ones.</summary>
    private sealed record InitBatch(
        bool IsMultipart,
        string R2Key,
        string? UploadUrl,
        string? MultipartUploadId,
        long PartSize,
        IReadOnlyList<string> PartUrls);

    /// <summary>Parses <c>init-batch</c>. Small files: <c>results.0.upload_url</c> + <c>r2_key</c> (single PUT).
    /// Large files: <c>type:"multipart"</c> with <c>upload_id</c>, <c>part_size</c>, <c>total_parts</c> and
    /// <c>initial_urls</c> (a <c>{"1":url,…}</c> map). Returns the parsed <see cref="InitBatch"/> or an error.</summary>
    private static (InitBatch? Init, string? Error) ParseInitBatch(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"storage.to init-batch failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (!TryFirstResult(response.Body, out JsonElement entry, out string? error))
        {
            return (null, error);
        }

        string? r2Key = entry.TryGetProperty("r2_key", out JsonElement k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(r2Key))
        {
            return (null, $"storage.to init-batch returned no r2_key: {Snippet(response.Body)}");
        }

        // Large files: storage.to splits into a Cloudflare-R2 multipart upload (one 32-MiB part per presigned URL).
        if (entry.TryGetProperty("type", out JsonElement type) && type.GetString() == "multipart")
        {
            string? uploadId = entry.TryGetProperty("upload_id", out JsonElement uid) ? uid.GetString() : null;
            long partSize = entry.TryGetProperty("part_size", out JsonElement ps) && ps.TryGetInt64(out long p) ? p : 0;
            int totalParts = entry.TryGetProperty("total_parts", out JsonElement tp) && tp.TryGetInt32(out int t) ? t : 0;
            if (string.IsNullOrEmpty(uploadId) || partSize <= 0 || totalParts <= 0
                || !entry.TryGetProperty("initial_urls", out JsonElement urls) || urls.ValueKind != JsonValueKind.Object)
            {
                return (null, $"storage.to init-batch multipart response was incomplete: {Snippet(response.Body)}");
            }

            string[] partUrls = new string[totalParts];
            for (int n = 1; n <= totalParts; n++)
            {
                if (!urls.TryGetProperty(n.ToString(CultureInfo.InvariantCulture), out JsonElement urlEl)
                    || urlEl.GetString() is not { Length: > 0 } url)
                {
                    return (null, $"storage.to init-batch multipart response missing the URL for part {n}: {Snippet(response.Body)}");
                }

                partUrls[n - 1] = url;
            }

            return (new InitBatch(true, r2Key, null, uploadId, partSize, partUrls), null);
        }

        string? uploadUrl = entry.TryGetProperty("upload_url", out JsonElement u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(uploadUrl))
        {
            return (null, $"storage.to init-batch returned no upload URL: {Snippet(response.Body)}");
        }

        return (new InitBatch(false, r2Key, uploadUrl, null, 0, []), null);
    }

    /// <summary>Parses <c>confirm-batch</c>: <c>{"success":true,"results":{"0":{"file":{"url":…}}}}</c>.
    /// Returns the public share link, or an error with a body snippet.</summary>
    private static (string? ShareUrl, string? Error) ParseConfirmBatch(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"storage.to confirm-batch failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (!TryFirstResult(response.Body, out JsonElement entry, out string? error))
        {
            return (null, error);
        }

        if (entry.TryGetProperty("file", out JsonElement file)
            && file.TryGetProperty("url", out JsonElement url)
            && url.GetString() is { Length: > 0 } shareUrl)
        {
            return (shareUrl, null);
        }

        return (null, $"storage.to confirm-batch returned no link: {Snippet(response.Body)}");
    }

    /// <summary>Both API responses wrap a single file as <c>results.0</c> (an object keyed "0", not an
    /// array). Pulls that entry out and checks the top-level + per-entry <c>success</c> flags.</summary>
    private static bool TryFirstResult(string body, out JsonElement entry, out string? error)
    {
        entry = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
            {
                error = $"storage.to reported failure: {Snippet(body)}";
                return false;
            }

            if (!root.TryGetProperty("results", out JsonElement results)
                || !results.TryGetProperty("0", out JsonElement first))
            {
                error = $"storage.to response had no results entry: {Snippet(body)}";
                return false;
            }

            // The batch is single-file here, so the per-entry success flag is the authoritative signal:
            // the server can return a top-level success:true with results.0.success:false. Surface the
            // entry's own reason when it does.
            if (first.TryGetProperty("success", out JsonElement entryOk) && entryOk.ValueKind == JsonValueKind.False)
            {
                string? reason = first.TryGetProperty("message", out JsonElement msg) ? msg.GetString()
                    : first.TryGetProperty("error", out JsonElement err) ? err.GetString()
                    : null;
                error = "storage.to rejected the file"
                    + (string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason)
                    + $" ({Snippet(body)})";
                return false;
            }

            // Clone so the element stays valid after the JsonDocument is disposed.
            entry = first.Clone();
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = $"storage.to response was not valid JSON: {Snippet(body)}";
            return false;
        }
    }

    private static Dictionary<string, string> BuildApiHeaders(string csrfToken, Dictionary<string, string> cookieJar) => new(StringComparer.Ordinal)
    {
        ["Accept"] = "application/json",
        ["X-CSRF-TOKEN"] = csrfToken,
        ["Origin"] = Host,
        ["Referer"] = HomeUrl,
        ["Cookie"] = BuildCookieHeader(cookieJar),
    };

    /// <summary>Merges a response's raw <c>Set-Cookie</c> lines (<c>name=value; attrs…</c>) into the
    /// jar, overwriting on name. The session cookie rotates on every response, so the latest wins.</summary>
    private static void MergeSetCookies(Dictionary<string, string> jar, IReadOnlyList<string> setCookies)
    {
        foreach (string raw in setCookies)
        {
            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            string pair = (semi < 0 ? raw : raw[..semi]).Trim();
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                jar[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
    }

    private static string BuildCookieHeader(Dictionary<string, string> jar)
        => string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));

    private Task<HttpResponseSnapshot> GetAsync(AttemptContext ctx, string url)
        => _getOverride is not null
            ? _getOverride(url, null)
            : ctx.Handler.GetSnapshotAsync(url, headers: null, ctx.Cancellation);

    private Task<HttpResponseSnapshot> PostJsonAsync(AttemptContext ctx, string url, string body, IReadOnlyDictionary<string, string> headers)
        => _postJsonOverride is not null
            ? _postJsonOverride(url, body, headers)
            : ctx.Handler.PostJsonAsync(url, body, headers, ctx.Cancellation);

    /// <summary>Runs the R2 PUT, funnelling byte progress into <paramref name="progress"/>. The
    /// production path bridges <see cref="HttpHandler.UploadProgress"/>; the test override calls the
    /// callback directly.</summary>
    private async Task<HttpResponseSnapshot> PutAsync(AttemptContext ctx, string url, string contentType, Action<long, long> progress, SpeedBudget? speedBudget)
    {
        if (_putOverride is not null)
        {
            return await _putOverride(ctx.FilePath, url, contentType, progress, speedBudget);
        }

        void OnProgress(object? _, OperationProgressEventArgs e) => progress(e.BytesProcessed, e.Size);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            return await ctx.Handler.UploadPutAsync(ctx.FilePath, url, contentType, headers: null, speedBudget, ctx.Cancellation);
        }
        finally
        {
            ctx.Handler.UploadProgress -= OnProgress;
        }
    }

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        // 2000 (not 200): API JSON responses are the whole diagnostic — a 200-char cut-off hid the actual
        // failure shape (e.g. storage.to's multipart response) even in the Log Details view.
        const int Max = 2000;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    [GeneratedRegex("""name=["']csrf-token["'][^>]*content=["']([^"']+)["']|content=["']([^"']+)["'][^>]*name=["']csrf-token["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex CsrfTokenRegex();

    /// <summary>
    /// Parts are order-independent here — server-issued presigned R2 part URLs — so they may be sent
    /// concurrently. Measured against live VikingFile on 2026-08-23: degree 8 reached 2.57x degree
    /// 1 and had not plateaued, so these hosts throttle per connection. Declared EXPLICITLY rather
    /// than relying on the interface default, which is not callable as a concrete-class member.
    /// The user's MaxParallelPartsPerFile setting caps this.
    /// </summary>
    public int MaxParallelPartsFor(Dal.FileHosterLoginDto credentials) => 8;
}
