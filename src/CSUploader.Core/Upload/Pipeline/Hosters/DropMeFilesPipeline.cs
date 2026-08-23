// <copyright file="DropMeFilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DropMeFiles (dropmefiles.com) — anonymous upload. No login, no captcha, 50 GB per file. Built
/// from the site's own <c>js/uploader.js</c> (plupload, like Webshare) plus a browser capture of a
/// real upload, 2026-08-01.
/// <list type="number">
///   <item><b>Scrape.</b> <c>GET /</c> for <c>SERVERID</c> — every call below is scoped to
///   <c>/s&lt;SERVERID&gt;/</c> and it rotates per page load.</item>
///   <item><b>Create the drop.</b> <c>POST /s&lt;id&gt;/upload/create</c> →
///   <c>{"result":"&lt;5-char uid&gt;","id":&lt;retention seconds&gt;}</c>.</item>
///   <item><b>Upload.</b> 4 MB chunks to
///   <c>/s&lt;id&gt;/uploadrmbl?name=…&amp;chunk=i&amp;chunks=n&amp;updir=&lt;uid&gt;</c> — see the
///   header note below.</item>
///   <item><b>Save.</b> <c>POST /s&lt;id&gt;/upload/save</c> with a plupload-shaped <c>files</c> JSON
///   array → <c>{"result":"Saved"}</c>. The link is <c>dropmefiles.com/&lt;uid&gt;</c>.</item>
/// </list>
/// <para>
/// <b>The chunk POST is the resumable nginx-upload protocol, not a plain body post.</b> Its body is
/// the raw slice, but three headers carry the actual protocol: <c>Session-ID</c> (a client-chosen id
/// the server accumulates chunks under), a matching <c>Content-Disposition: attachment;
/// filename="&lt;session-id&gt;"</c>, and <c>Content-Range: bytes start-end/total</c>. Without them
/// the node answers <b>415 Unsupported Media Type</b> regardless of content type — which is what it
/// answers to every naive raw POST, and why this needed a capture. Intermediate chunks reply
/// <c>201 Created</c> with the accumulated range; the last replies <c>200</c> with JSON.
/// </para>
/// <para>
/// <b>One drop per file.</b> A drop is a folder with ONE link covering everything in it, but this
/// app models a link per file, so each upload creates its own. That is also what the host's
/// anti-abuse sees, and it is not free: probing it produced
/// <c>{"error":{"code":99,"message":"Spam"}}</c> from <c>upload/create</c> after about ten calls
/// from one address. Uploads are therefore serialised (<see cref="MaxConcurrentUploads"/> = 1) to
/// look like a person rather than a swarm.
/// </para>
/// <para>
/// <b>Links expire.</b> This is a transfer service: retention is chosen per drop and the longest
/// available is 14 days, which is what this sends. Files are gone after that — it is not durable
/// hosting, and the link will die under a user who treats it as such.
/// </para>
/// <para>
/// <b>The upload route varies</b>, exactly as the site's own <c>BeforeUpload</c> picks it: archive
/// and executable extensions at or under 75 MB go to <c>/uploadch</c> (a virus-scan path) and
/// anything over 50 GB to <c>/uploadsl</c>; everything else to <c>/uploadrmbl</c>. The cap makes the
/// third unreachable, but the scan route is very much reachable — a 50 MB <c>.rar</c> takes it.
/// </para>
/// </summary>
public sealed class DropMeFilesPipeline : IFileHosterPipeline
{
    private const string Host = "https://dropmefiles.com";
    private const string HomeUrl = Host + "/";

    /// <summary>The site's own <c>CHUNKSIZE</c> of '4m'.</summary>
    private const long ChunkSize = 4 * 1024 * 1024;

    /// <summary>The site's own <c>SPEEDDOWNSIZE</c>, and the cap its page advertises ("up to 50 Gb").</summary>
    private const long MaxUploadSize = 53_687_091_200;

    /// <summary>The site's own <c>MAXSCANSIZE</c> — the ceiling for the virus-scan upload route.</summary>
    private const long MaxScanSize = 78_643_200;

    /// <summary>Retention: 0 = until first download, 1 = 3 days, 2 = 7 days, 3 = 14 days. The longest
    /// is the only sensible choice for an uploader.</summary>
    private const string RetentionPeriod = "3";

    /// <summary>Extensions the site routes through its scanning endpoint — copied verbatim from
    /// <c>needCheckFileExt</c> in its uploader.js.</summary>
    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "apk", "bat", "bin", "cgi", "com", "cpp", "exe", "gadget", "jar",
        "msi", "msu", "pif", "scr", "vb", "wsf", "action", "app", "command", "csh", "ipa", "workflow",
    };

    private static readonly Regex ServerIdRegex = new(
        @"var\s+SERVERID\s*=\s*'(?<id>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, long, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public DropMeFilesPipeline()
    {
    }

    /// <summary>Test ctor — drives the scrape, the two form posts and each chunk from canned responses.</summary>
    internal DropMeFilesPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, IReadOnlyDictionary<string, string>, long, long, Task<HttpResponseSnapshot>> chunkOverride)
    {
        _getOverride = getOverride;
        _postFormOverride = postFormOverride;
        _chunkOverride = chunkOverride;
    }

    public string Name => "DropMeFiles";

    /// <summary>Downloads are captcha-free: a fresh anonymous session fetched the server-
    /// rendered per-file URL straight to the bytes; the securimage overlay arms only for
    /// flagged traffic (live probe, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>50 GB — the site's own figure, both as the page's text and as <c>SPEEDDOWNSIZE</c>.</summary>
    public long? MaxFileSize => MaxUploadSize;

    /// <summary>14 days - the longest retention it offers (its options are until-first-download, 3, 7
    /// and 14 days) and what this app sends. This is a transfer service, not storage: the files are
    /// gone afterwards.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.DaysAfterUpload(14);

    public int? MaxFilesPerPackage => null;

    /// <summary>
    /// Five at a time, whatever the account. Each file needs its own drop, and the host's anti-abuse
    /// answers a burst of <c>upload/create</c> calls with "Spam" — so this is capped rather than
    /// unlimited. Five is a judgement, not a measured limit: the refusal was seen at roughly ten
    /// rapid creates while probing, and whether it counts concurrency, rate or total was never
    /// established. If a batch starts failing with "Spam", lower it.
    /// </summary>
    public int? MaxConcurrentUploadsFor(Dal.FileHosterLoginDto credentials) => 5;

    /// <summary>No account exists to attach an upload to — the drop is the whole identity.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>DropMeFiles has no login anywhere on the site, so the Add Account dialog leaves it out
    /// of its hoster list — there is nothing to add.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: which server bank is this session on ===
        (string? serverId, string? cookies, string? scrapeError) = await GetServerIdAsync(ctx);
        if (serverId is null)
        {
            yield return new AttemptFailed(scrapeError!, null);
            yield break;
        }

        // === Step 2: create this file's drop ===
        (string? uid, string? createError) = await CreateDropAsync(ctx, serverId, cookies);
        if (uid is null)
        {
            yield return new AttemptFailed(createError!, null);
            yield break;
        }

        // === Step 3: send the bytes ===
        // The file id is minted ONCE and used by both steps. The chunk upload's Session-ID is
        // "<uid>_<fileId>" and save's files[0].id must be that same <fileId> — that pairing is how the
        // server matches the saved record to the bytes it spooled. Generating a second id for save
        // instead leaves the upload orphaned: every request still succeeds, save still answers
        // "Saved", and the drop page then reads "Files were deleted due to unexpected error while
        // uploading". Verified the hard way, 2026-08-01.
        string fileId = $"o_{Guid.NewGuid():N}";

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<string?> workTask = UploadChunksAsync(ctx, serverId, uid, fileId, cookies);

        _ = workTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // A transport fault propagates raw to AttemptRunner, which re-runs this pipeline and creates a
        // FRESH drop — the abandoned one holds no saved file and expires on its own, so no double-create.
        string? uploadError = await workTask;
        if (uploadError is not null)
        {
            yield return new AttemptFailed(uploadError, null);
            yield break;
        }

        // === Step 4: save — without this the bytes are on the server but the drop stays empty ===
        string? saveError = await SaveDropAsync(ctx, serverId, uid, fileId, cookies);
        if (saveError is not null)
        {
            yield return new AttemptFailed(saveError, null);
            yield break;
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires in 14 days — this host does not keep files.");
        yield return new TransferCompleted(Host + "/" + uid);
    }

    /// <summary>
    /// DropMeFiles has no accounts at all — there is nothing to sign into. Say so rather than failing
    /// silently if someone adds one in Settings.
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
            "DropMeFiles has no accounts — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Reads <c>var SERVERID = '3'</c> out of the homepage. Internal for testing.</summary>
    internal static string? ParseServerId(string html)
    {
        Match m = ServerIdRegex.Match(html);
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>
    /// Reads <c>{"result":"&lt;uid&gt;"}</c>, or the API's own refusal out of
    /// <c>{"error":{"code":99,"message":"Spam"}}</c> — which is what a burst of creates earns, and is
    /// worth surfacing verbatim rather than as "upload failed". Internal for testing.
    /// </summary>
    internal static (string? Uid, string? Error) ParseCreateResponse(string json, int statusCode)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                {
                    string? message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    return (null, $"DropMeFiles refused to start an upload: {message ?? "unknown error"}."
                        + (string.Equals(message, "Spam", StringComparison.OrdinalIgnoreCase)
                            ? " Its anti-abuse rejects rapid uploads from one address; try again later."
                            : string.Empty));
                }

                if (root.TryGetProperty("result", out JsonElement result)
                    && result.ValueKind == JsonValueKind.String
                    && result.GetString() is { Length: > 0 } uid)
                {
                    return (uid, null);
                }
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return (null, $"DropMeFiles did not return a drop id (HTTP {statusCode}): {Snippet(json)}");
    }

    /// <summary>
    /// A chunk is accepted as <c>201 Created</c> (more to come) or <c>200</c> with
    /// <c>{"result":null}</c> (that was the last). Anything else is a failure — most usefully
    /// <c>415</c>, which is what the node says when the resumable headers are missing or wrong.
    /// Internal for testing.
    /// </summary>
    internal static string? ValidateChunkResponse(HttpResponseSnapshot response, bool isLast)
    {
        if (!isLast)
        {
            return response.StatusCode == 201
                ? null
                : $"DropMeFiles rejected a chunk (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        if (response.StatusCode != 200)
        {
            return $"DropMeFiles rejected the final chunk (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind != JsonValueKind.Null)
            {
                return "DropMeFiles refused the file: " + Snippet(error.ToString());
            }
        }
        catch (JsonException)
        {
            return $"DropMeFiles returned an unreadable answer to the final chunk: {Snippet(response.Body)}";
        }

        return null;
    }

    /// <summary>
    /// The upload route the site's own <c>BeforeUpload</c> would pick for this file. Internal for testing.
    /// </summary>
    internal static string UploadRouteFor(string fileName, long fileSize)
    {
        if (fileSize > MaxUploadSize)
        {
            return "uploadsl";
        }

        string ext = Path.GetExtension(fileName).TrimStart('.');
        return ScannedExtensions.Contains(ext) && fileSize <= MaxScanSize ? "uploadch" : "uploadrmbl";
    }

    /// <summary>
    /// The <c>files</c> array <c>upload/save</c> expects — a serialised plupload file object. Every
    /// field here was present in the captured request; <c>status: 5</c> is plupload's DONE and
    /// <c>logstatus: 2</c> is the site's own "uploaded" marker. Internal for testing.
    /// </summary>
    internal static string BuildFilesJson(string fileId, string fileName, long size, string uid, string lastModifiedIso, long completedUnixMs)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("id", fileId);
            writer.WriteString("name", fileName);
            writer.WriteString("type", MimeTypeGuesser.Guess(fileName));
            writer.WriteString("relativePath", string.Empty);
            writer.WriteNumber("size", size);
            writer.WriteNumber("origSize", size);
            writer.WriteNumber("loaded", size);
            writer.WriteNumber("percent", 100);
            writer.WriteNumber("status", 5);
            writer.WriteString("lastModifiedDate", lastModifiedIso);
            writer.WriteNumber("completeTimestamp", completedUnixMs);
            writer.WriteString("dir", uid);
            writer.WriteNumber("logstatus", 2);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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

    private static Dictionary<string, string> AjaxHeaders(string? cookies)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
        };

        if (!string.IsNullOrEmpty(cookies))
        {
            headers["Cookie"] = cookies;
        }

        return headers;
    }

    private async Task<(string? ServerId, string? Cookies, string? Error)> GetServerIdAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = _getOverride is not null
                ? await _getOverride(HomeUrl, null)
                : await ctx.Handler.GetSnapshotAsync(HomeUrl, null, ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, "DropMeFiles homepage fetch failed: " + ex.Message);
        }

        string? id = ParseServerId(snap.Body);
        string cookies = string.Join("; ", snap.SetCookies.Select(c => c.Split(';')[0].Trim()).Where(c => c.Length > 0));

        return id is null
            ? (null, null, $"DropMeFiles homepage carried no SERVERID (HTTP {snap.StatusCode}).")
            : (id, cookies, null);
    }

    private async Task<(string? Uid, string? Error)> CreateDropAsync(AttemptContext ctx, string serverId, string? cookies)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["runtime"] = "html5",
            ["server"] = "0",
            ["group"] = string.Empty,
            ["updirType"] = "abc",
            ["count"] = "1",
            ["size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
            ["period"] = RetentionPeriod,
            ["name"] = string.Empty,
            ["comment"] = string.Empty,
        };

        string url = $"{Host}/s{serverId}/upload/create";

        HttpResponseSnapshot snap;
        try
        {
            snap = _postFormOverride is not null
                ? await _postFormOverride(url, form, AjaxHeaders(cookies))
                : await ctx.Handler.PostFormAsync(url, form, AjaxHeaders(cookies), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "DropMeFiles create request failed: " + ex.Message);
        }

        return ParseCreateResponse(snap.Body, snap.StatusCode);
    }

    /// <summary>
    /// Streams the file as 4 MB chunks. The three resumable headers are the protocol — see the class
    /// remarks — and the session id ties them together across chunks.
    /// </summary>
    private async Task<string?> UploadChunksAsync(AttemptContext ctx, string serverId, string uid, string fileId, string? cookies)
    {
        long total = ctx.FileSize;
        int chunks = total == 0 ? 1 : (int)((total + ChunkSize - 1) / ChunkSize);
        string sessionId = $"{uid}_{fileId}";
        string route = UploadRouteFor(ctx.FileName, total);
        string url = $"{Host}/s{serverId}/{route}"
            + $"?name={Uri.EscapeDataString(ctx.FileName)}&chunk={{0}}&chunks={chunks.ToString(CultureInfo.InvariantCulture)}&updir={uid}";
        DateTime started = DateTime.Now;

        FileStream? file = _chunkOverride is null
            ? new FileStream(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true)
            : null;

        try
        {
            for (int index = 0; index < chunks; index++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();

                long offset = (long)index * ChunkSize;
                long len = Math.Min(ChunkSize, total - offset);
                bool isLast = index == chunks - 1;

                Dictionary<string, string> headers = AjaxHeaders(cookies);
                headers["Session-ID"] = sessionId;
                headers["Content-Disposition"] = $"attachment; filename=\"{sessionId}\"";
                headers["Content-Range"] = string.Create(
                    CultureInfo.InvariantCulture, $"bytes {offset}-{offset + len - 1}/{total}");

                string chunkUrl = string.Format(CultureInfo.InvariantCulture, url, index);

                HttpResponseSnapshot resp;
                try
                {
                    resp = _chunkOverride is not null
                        ? await _chunkOverride(chunkUrl, headers, offset, len)
                        : await ctx.Handler.PutChunkAsync(
                            chunkUrl,
                            new ChunkSliceStream(file!, len),
                            len,
                            offset,
                            total,
                            started,
                            headers,
                            ctx.SpeedBudget,
                            ctx.Cancellation,
                            HttpMethod.Post);
                }
                catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
                {
                    throw;
                }

                if (ValidateChunkResponse(resp, isLast) is { } error)
                {
                    return error;
                }
            }
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<string?> SaveDropAsync(AttemptContext ctx, string serverId, string uid, string fileId, string? cookies)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["files"] = BuildFilesJson(
                fileId,
                ctx.FileName,
                ctx.FileSize,
                uid,
                now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                new DateTimeOffset(now).ToUnixTimeMilliseconds()),
            ["uid"] = uid,
            ["group"] = string.Empty,
            ["count"] = "1",
            ["size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
            ["speed"] = "0",
            ["period"] = RetentionPeriod,
            ["name"] = string.Empty,
            ["comment"] = string.Empty,
        };

        string url = $"{Host}/s{serverId}/upload/save";

        HttpResponseSnapshot snap;
        try
        {
            snap = _postFormOverride is not null
                ? await _postFormOverride(url, form, AjaxHeaders(cookies))
                : await ctx.Handler.PostFormAsync(url, form, AjaxHeaders(cookies), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "DropMeFiles save request failed: " + ex.Message;
        }

        return snap.Body.Contains("Saved", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"DropMeFiles did not save the upload (HTTP {snap.StatusCode}): {Snippet(snap.Body)}";
    }
}
