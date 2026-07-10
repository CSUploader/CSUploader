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
/// </summary>
public sealed partial class StorageToPipeline : IFileHosterPipeline
{
    private const string Host = "https://storage.to";
    private const string HomeUrl = Host + "/";
    private const string InitBatchUrl = Host + "/api/upload/init-batch";
    private const string ConfirmBatchUrl = Host + "/api/upload/confirm-batch";

    /// <summary>Anonymous per-file cap — 25 GB, the figure storage.to advertises for no-signup
    /// uploads (storage.to/send-large-files). The server is the real gate (it can reject at
    /// <c>init-batch</c>, before any bytes go up), but failing fast here skips a guaranteed-doomed
    /// round-trip on an obviously-too-big file. Decimal GB to match the marketing number.</summary>
    private const long AnonymousMaxFileSizeBytes = 25L * 1000 * 1000 * 1000;

    // <meta name="csrf-token" content="..."> on the homepage — tolerant of attribute order.
    private static readonly Regex _csrfTokenRegex = CsrfTokenRegex();

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, string, Action<long, long>, Func<long?>?, Task<HttpResponseSnapshot>>? _putOverride;

    public StorageToPipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET, the two JSON API POSTs, and the R2 PUT from canned
    /// responses so the bootstrap/init/transfer/confirm orchestration runs without the network. The PUT
    /// override is handed the progress callback so a test can exercise the TransferProgress bridge.</summary>
    internal StorageToPipeline(
        Func<string, HttpResponseSnapshot> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postJsonOverride,
        Func<string, string, string, Action<long, long>, HttpResponseSnapshot> putOverride)
    {
        _getOverride = (url, _) => Task.FromResult(getOverride(url));
        _postJsonOverride = (url, body, headers) => Task.FromResult(postJsonOverride(url, body, headers));
        _putOverride = (filePath, url, contentType, progress, _) => Task.FromResult(putOverride(filePath, url, contentType, progress));
    }

    public string Name => "Storage.to";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

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
        (string? uploadUrl, string? r2Key, string? initError) = ParseInitBatch(initResponse);
        if (uploadUrl is null || r2Key is null)
        {
            yield return new AttemptFailed(initError ?? "storage.to init-batch returned no upload URL", null);
            yield break;
        }

        // === Step 3: PUT the bytes straight to Cloudflare R2 ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        var stopwatch = Stopwatch.StartNew();
        void Progress(long sent, long total)
        {
            double speed = sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progressChannel.Writer.TryWrite(new TransferProgress(sent, total, speed));
        }

        Task<HttpResponseSnapshot> putTask = PutAsync(ctx, uploadUrl, contentType, Progress, ctx.SpeedLimitProvider);

        _ = putTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        // Let a transport fault propagate out of RunAsync to the shared retry layer (AttemptRunner):
        // UploadPutAsync reclassifies a connect-phase/mid-send abort as a safe-to-retry
        // UploadBodyTransferException, and re-running the whole pipeline gets a FRESH init-batch +
        // presigned URL. It never double-creates because the file record is only made by confirm-batch
        // below, which a failed PUT never reaches. A server verdict (non-2xx) does NOT throw.
        HttpResponseSnapshot putResponse = await putTask;
        if (putResponse.StatusCode is < 200 or >= 300)
        {
            yield return new AttemptFailed(
                $"storage.to R2 upload failed (HTTP {putResponse.StatusCode}): {Snippet(putResponse.Body)}",
                null);
            yield break;
        }

        // Land the bar on 100% (the R2 PUT reports progress off the request-body write, which can finish
        // a beat before the response returns).
        yield return new TransferProgress(ctx.FileSize, ctx.FileSize, ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        // === Step 4: confirm-batch — register the uploaded object and get the share link ===
        long uploadSpeed = (long)(ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
        string confirmBody = JsonSerializer.Serialize(new
        {
            files = new[] { new { filename = ctx.FileName, size = ctx.FileSize, content_type = contentType, r2_key = r2Key } },
            collection_id = (string?)null,
            upload_speed = uploadSpeed,
            as_temp = false,
        });

        HttpResponseSnapshot? confirmResponse = null;
        string? confirmRequestError = null;
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
            confirmRequestError = "storage.to confirm-batch request failed: " + ex.Message;
        }

        if (confirmResponse is null)
        {
            yield return new AttemptFailed(confirmRequestError ?? "storage.to confirm-batch request failed", null);
            yield break;
        }

        (string? shareUrl, string? confirmError) = ParseConfirmBatch(confirmResponse);
        if (shareUrl is null)
        {
            yield return new AttemptFailed(confirmError ?? "storage.to confirm-batch returned no link", null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl);
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

    /// <summary>Parses <c>init-batch</c>: <c>{"success":true,"results":{"0":{"upload_url":…,"r2_key":…}}}</c>.
    /// Returns the presigned URL + key, or an error with a body snippet.</summary>
    private static (string? UploadUrl, string? R2Key, string? Error) ParseInitBatch(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"storage.to init-batch failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (!TryFirstResult(response.Body, out JsonElement entry, out string? error))
        {
            return (null, null, error);
        }

        string? uploadUrl = entry.TryGetProperty("upload_url", out JsonElement u) ? u.GetString() : null;
        string? r2Key = entry.TryGetProperty("r2_key", out JsonElement k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(r2Key))
        {
            return (null, null, $"storage.to init-batch returned no upload URL: {Snippet(response.Body)}");
        }

        return (uploadUrl, r2Key, null);
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
    private async Task<HttpResponseSnapshot> PutAsync(AttemptContext ctx, string url, string contentType, Action<long, long> progress, Func<long?>? getBytesPerSecond)
    {
        if (_putOverride is not null)
        {
            return await _putOverride(ctx.FilePath, url, contentType, progress, getBytesPerSecond);
        }

        void OnProgress(object? _, OperationProgressEventArgs e) => progress(e.BytesProcessed, e.Size);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            return await ctx.Handler.UploadPutAsync(ctx.FilePath, url, contentType, headers: null, getBytesPerSecond, ctx.Cancellation);
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
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    [GeneratedRegex("""name=["']csrf-token["'][^>]*content=["']([^"']+)["']|content=["']([^"']+)["'][^>]*name=["']csrf-token["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex CsrfTokenRegex();
}
