// <copyright file="FilehosterIoPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// filehoster.io upload pipeline — anonymous (no-login) uploads. filehoster.io is an XFileSharing host
/// running the "xfspro" chunked-upload plugin; uploading needs no account (verified against a live
/// anonymous round-trip 2026-06-29). Per file:
/// <list type="number">
///   <item><b>SID.</b> The client mints a 16-digit random upload SID (matches
///   <c>upload-xfspro.js</c>).</item>
///   <item><b>start_upload.</b> POST <c>https://filehoster.io/</c> (form-urlencoded
///   <c>op=start_upload&amp;file_name&amp;file_descr=&amp;file_public=1&amp;file_size</c>) →
///   <c>{"url":"https://filehoster.io/cgi-bin","plugin":"xfspro"}</c>. The <c>url</c> is the CGI base.</item>
///   <item><b>Chunks.</b> Split the file into ≤100 MiB chunks (the Cloudflare request-body cap the JS
///   targets) and PUT each, in order, to <c>&lt;url&gt;/put_chunk.cgi</c> as a raw
///   <c>application/octet-stream</c> body carrying an <c>X-Upload-SID</c> header. Each replies
///   <c>{"status":"OK"}</c>; the server appends by SID (no offset/range is sent).</item>
///   <item><b>import_file.</b> POST <c>&lt;url&gt;/api.cgi</c> (form-urlencoded
///   <c>op=import_file&amp;sid&amp;fname&amp;sess_id=&amp;…</c>; <c>sess_id</c> is empty for anonymous)
///   → <c>{"file_code":"…","links":{"download_link":"…"}}</c>. The share link is
///   <c>links.download_link</c> (<c>https://filehoster.io/&lt;file_code&gt;/&lt;name&gt;.html</c>).</item>
/// </list>
/// No hashing, no cookies (anonymous needs none). Anonymous cap is 10 GB. <c>import_file</c> is the only
/// record-creating step — a failed chunk before it leaves only orphaned temp data, so a mid-send abort
/// is safe to retry the whole pipeline (a fresh SID discards the partial; see
/// <see cref="HttpHandler.PutChunkAsync"/>). The account path isn't wired up (uploads use the wizard's
/// built-in Anonymous option).
/// </summary>
public sealed class FilehosterIoPipeline : IFileHosterPipeline
{
    private const string Host = "https://filehoster.io";
    private const string HomeUrl = Host + "/";
    private const string PutChunkPath = "/put_chunk.cgi";
    private const string ApiPath = "/api.cgi";

    /// <summary>Anonymous per-file cap — 10 GB ("Max file size: 10GB" on the upload page). The server
    /// is the real gate; this fails fast on an obviously-too-big file. Decimal GB to match the page.</summary>
    private const long AnonymousMaxFileSizeBytes = 10L * 1000 * 1000 * 1000;

    /// <summary>Chunk size — 100 MiB, the Cloudflare request-body cap <c>upload-xfspro.js</c> defaults
    /// to. Files larger than this are split into multiple sequential PUTs under one SID.</summary>
    private const long ChunkSizeBytes = 100L * 1024 * 1024;

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, long, long, long, Action<long, long>, Task<HttpResponseSnapshot>>? _chunkPutOverride;

    public FilehosterIoPipeline()
    {
    }

    /// <summary>Test ctor — drives the two form POSTs (start_upload, import_file) and the per-chunk PUT
    /// from canned responses so the orchestration runs without the network. The chunk override is handed
    /// the SID, the chunk's base offset/length, and the progress callback.</summary>
    internal FilehosterIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
    {
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _chunkPutOverride = (url, sid, basePos, len, total, progress) => Task.FromResult(chunkPutOverride(url, sid, basePos, len, total, progress));
    }

    public string Name => "Filehoster.io";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>filehoster.io accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: anonymous per-file size cap ===
        if (ctx.FileSize > AnonymousMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds filehoster.io's anonymous {ByteUnit.FromBytes(AnonymousMaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // A fresh SID per attempt is what makes a retry safe: re-running picks a new SID and the previous
        // partial upload is orphaned, so import_file (the only record-creating step) never double-creates.
        string sid = GenerateSid();

        // === Step 1: start_upload → the CGI base URL ===
        HttpResponseSnapshot? startResponse = null;
        string? startRequestError = null;
        try
        {
            startResponse = await PostFormAsync(ctx, HomeUrl, BuildStartUploadForm(ctx));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startRequestError = "filehoster.io start_upload request failed: " + ex.Message;
        }

        if (startResponse is null)
        {
            yield return new AttemptFailed(startRequestError ?? "filehoster.io start_upload request failed", null);
            yield break;
        }

        (string? baseUrl, string? startError) = ParseStartUpload(startResponse);
        if (baseUrl is null)
        {
            yield return new AttemptFailed(startError ?? "filehoster.io start_upload returned no upload URL", null);
            yield break;
        }

        // === Step 2: PUT the bytes in ≤100 MiB chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        var stopwatch = Stopwatch.StartNew();
        void Progress(long sent, long total)
        {
            double speed = sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progressChannel.Writer.TryWrite(new TransferProgress(sent, total, speed));
        }

        Task<(bool Ok, string? Error)> chunkTask = UploadChunksAsync(ctx, baseUrl, sid, Progress);

        _ = chunkTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        // A chunk transport fault surfaces here as a retryable UploadBodyTransferException (PutChunkAsync
        // reclassifies it) and propagates out of RunAsync so the shared retry layer re-runs against a
        // fresh SID. A server verdict (non-2xx / not-OK chunk) does NOT throw — it comes back as Ok=false.
        (bool ok, string? chunkError) = await chunkTask;
        if (!ok)
        {
            yield return new AttemptFailed(chunkError ?? "filehoster.io chunk upload failed", null);
            yield break;
        }

        yield return new TransferProgress(ctx.FileSize, ctx.FileSize, ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        // === Step 3: import_file → the share link ===
        HttpResponseSnapshot? importResponse = null;
        string? importRequestError = null;
        try
        {
            importResponse = await PostFormAsync(ctx, baseUrl + ApiPath, BuildImportFileForm(ctx, sid));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            importRequestError = "filehoster.io import_file request failed: " + ex.Message;
        }

        if (importResponse is null)
        {
            yield return new AttemptFailed(importRequestError ?? "filehoster.io import_file request failed", null);
            yield break;
        }

        (string? shareUrl, string? importError) = ParseImportFile(importResponse);
        if (shareUrl is null)
        {
            yield return new AttemptFailed(importError ?? "filehoster.io import_file returned no link", null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl);
    }

    /// <summary>
    /// filehoster.io login isn't wired up — uploads use the anonymous path. Surface a clear message
    /// rather than a silent failure if someone tries to add a filehoster.io account in Settings.
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
            "filehoster.io login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Mints a 16-digit decimal upload SID, matching <c>upload-xfspro.js</c>
    /// (<c>Array(16).fill().map(() =&gt; Math.floor(Math.random()*10)).join("")</c>).</summary>
    private static string GenerateSid()
    {
        char[] digits = new char[16];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + Random.Shared.Next(10));
        }

        return new string(digits);
    }

    /// <summary>Uploads the file as one or more ≤100 MiB chunks, in order, under <paramref name="sid"/>.
    /// Returns (false, error) on a server verdict; throws (retryable) on a chunk transport fault.</summary>
    private async Task<(bool Ok, string? Error)> UploadChunksAsync(AttemptContext ctx, string baseUrl, string sid, Action<long, long> progress)
    {
        long total = ctx.FileSize;
        int chunkCount = (int)Math.Max(1, (total + ChunkSizeBytes - 1) / ChunkSizeBytes);
        string chunkUrl = baseUrl + PutChunkPath;

        if (_chunkPutOverride is not null)
        {
            for (int i = 0; i < chunkCount; i++)
            {
                long basePos = (long)i * ChunkSizeBytes;
                long len = Math.Min(ChunkSizeBytes, total - basePos);
                HttpResponseSnapshot resp = await _chunkPutOverride(chunkUrl, sid, basePos, len, total, progress);
                (bool ok, string? error) = CheckChunkResponse(resp, i);
                if (!ok)
                {
                    return (false, error);
                }
            }

            return (true, null);
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["X-Upload-SID"] = sid };
        DateTime started = DateTime.Now;
        void OnProgress(object? _, OperationProgressEventArgs e) => progress(e.BytesProcessed, e.Size);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            await using FileStream fs = new(ctx.FilePath, FileMode.Open, FileAccess.Read);
            for (int i = 0; i < chunkCount; i++)
            {
                long basePos = (long)i * ChunkSizeBytes;
                long len = Math.Min(ChunkSizeBytes, total - basePos);

                // ChunkSliceStream serves exactly `len` bytes from the shared FileStream (whose position
                // advances as each slice is consumed) and never disposes it — the FileStream lives across
                // all chunks. The PUT's content disposes the slice; that's a no-op for the inner stream.
                ChunkSliceStream slice = new(fs, len);
                HttpResponseSnapshot resp = await ctx.Handler.PutChunkAsync(
                    chunkUrl, slice, len, basePos, total, started, headers, ctx.SpeedLimitProvider, ctx.Cancellation);
                (bool ok, string? error) = CheckChunkResponse(resp, i);
                if (!ok)
                {
                    return (false, error);
                }
            }

            return (true, null);
        }
        finally
        {
            ctx.Handler.UploadProgress -= OnProgress;
        }
    }

    /// <summary>A chunk PUT succeeds with HTTP 2xx and a body echoing <c>"status":"OK"</c>.</summary>
    private static (bool Ok, string? Error) CheckChunkResponse(HttpResponseSnapshot resp, int index)
    {
        if (resp.StatusCode is < 200 or >= 300)
        {
            return (false, $"filehoster.io chunk {index} failed (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
        }

        // Success is {"status":"OK"}. Parse and check the status field specifically (mirrors
        // ParseStartUpload/ParseImportFile) so an error envelope that merely carries the token "OK" in
        // some other field — e.g. {"status":"error","note":"OK"} — can't be read as success.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.TryGetProperty("status", out JsonElement status)
                && string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (false, $"filehoster.io chunk {index} was not accepted: {Snippet(resp.Body)}");
    }

    private static (string? BaseUrl, string? Error) ParseStartUpload(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"filehoster.io start_upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("url", out JsonElement url) && url.GetString() is { Length: > 0 } baseUrl)
            {
                return (baseUrl.TrimEnd('/'), null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (null, $"filehoster.io start_upload returned no upload URL: {Snippet(response.Body)}");
    }

    private static (string? ShareUrl, string? Error) ParseImportFile(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"filehoster.io import_file failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("links", out JsonElement links)
                && links.TryGetProperty("download_link", out JsonElement dl)
                && dl.GetString() is { Length: > 0 } link)
            {
                return (link, null);
            }

            // Fallback: synthesise the link from the file code if the links block is absent.
            if (root.TryGetProperty("file_code", out JsonElement fc) && fc.GetString() is { Length: > 0 } code)
            {
                return ($"{Host}/{code}", null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (null, $"filehoster.io import_file returned no link: {Snippet(response.Body)}");
    }

    private static Dictionary<string, string> BuildStartUploadForm(AttemptContext ctx) => new(StringComparer.Ordinal)
    {
        ["op"] = "start_upload",
        ["file_name"] = ctx.FileName,
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["file_size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
    };

    private static Dictionary<string, string> BuildImportFileForm(AttemptContext ctx, string sid) => new(StringComparer.Ordinal)
    {
        ["op"] = "import_file",
        ["sid"] = sid,
        ["fname"] = ctx.FileName,
        ["sess_id"] = string.Empty, // empty = anonymous
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
    };

    private Task<HttpResponseSnapshot> PostFormAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string> form)
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : ctx.Handler.PostFormAsync(url, form, ctx.Cancellation);

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
}
