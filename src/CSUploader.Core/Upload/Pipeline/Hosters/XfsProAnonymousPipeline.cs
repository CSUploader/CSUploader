// <copyright file="XfsProAnonymousPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Shared base for XFileSharing hosts running the <b>xfspro</b> chunked plugin in its keyless
/// (<c>GET /server</c>) form, where an upload needs no account at all:
/// <list type="number">
///   <item><b>Node.</b> <c>GET /server</c> → <c>{"url":"https://sNN.host/cgi-bin"}</c> — no key, no
///   cookie, no <c>op=start_upload</c>.</item>
///   <item><b>Chunks.</b> PUT ≤100 MiB slices to <c>&lt;url&gt;/put_chunk.cgi</c> as a raw
///   octet-stream carrying an <c>X-Upload-SID</c> header (client-minted, 16 digits). Each replies
///   <c>{"status":"OK"}</c>; the server appends by SID, so no offsets are sent.</item>
///   <item><b>Finalise.</b> Multipart POST to <c>&lt;url&gt;/api.cgi</c> with <c>op=import_file</c>
///   and an <b>empty <c>sess_id</c></b> — which is the entire anonymous signal. A captured anonymous
///   and signed-in upload on FILEAXA differed in nothing else.</item>
/// </list>
/// <para>
/// Extracted when the third host on this protocol turned up (FILEAXA, DailyUploads, and
/// filehoster.io's older variant). Subclasses supply a name and a host; everything else is shared.
/// <see cref="FilehosterIoPipeline"/> is deliberately NOT ported onto this: it uses the earlier
/// <c>POST op=start_upload</c> discovery and a form-urlencoded finalise, and it is account-only with
/// its own login and storage scraping. Fold it in only if it needs changing anyway.
/// </para>
/// <para>
/// <b>Nothing exists server-side until the finalise.</b> A chunk fault is therefore safe for the
/// shared retry layer to re-run: a retry mints a fresh SID and orphans the partial upload rather
/// than double-creating.
/// </para>
/// </summary>
public abstract class XfsProAnonymousPipeline : IFileHosterPipeline
{
    private const string PutChunkPath = "/put_chunk.cgi";
    private const string ApiPath = "/api.cgi";

    /// <summary>The Cloudflare request-body ceiling this family's JS targets, and what
    /// <see cref="FilehosterIoPipeline"/> uses.</summary>
    private const long ChunkSizeBytes = 100L * 1024 * 1024;

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _finaliseOverride;
    private readonly Func<string, long, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    protected XfsProAnonymousPipeline()
    {
    }

    /// <summary>Test ctor — drives node lookup, each chunk and the finalise from canned responses.</summary>
    internal XfsProAnonymousPipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, long, long, Task<HttpResponseSnapshot>> chunkOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> finaliseOverride)
    {
        _getOverride = getOverride;
        _chunkOverride = chunkOverride;
        _finaliseOverride = finaliseOverride;
    }

    public abstract string Name { get; }

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>
    /// Null by default — these hosts publish no figure a client can read (<c>?op=api_get_limits</c>
    /// answers with the homepage), and a guessed cap rejects files the server would take. Note this
    /// deliberately overrides nothing from <see cref="XFileSharingApiPipeline"/>, whose 1 GiB default
    /// silently skips larger files.
    /// </summary>
    public virtual long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous IS the path — <c>sess_id</c> simply goes out empty.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>Site root, no trailing slash (e.g. <c>https://dailyuploads.net</c>).</summary>
    protected abstract string Host { get; }

    /// <summary>Where the keyless node lookup lives. The whole family answers <c>/server</c>.</summary>
    protected virtual string ServerUrl => Host + "/server";

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        (string? cgiBase, string? nodeError) = await GetNodeAsync(ctx);
        if (cgiBase is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Url, string? Error)> workTask = UploadAndFinaliseAsync(ctx, cgiBase);

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

        (string? url, string? error) = await workTask;
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Accounts aren't wired up on any host using this base. All an account changes on the wire is a
    /// non-empty <c>sess_id</c>; obtaining one needs the XFileSharing login flow, which lives in
    /// <see cref="XFileSharingApiPipeline"/> and isn't carried here.
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
            $"{Name} login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Reads <c>{"url":"https://sNN.host/cgi-bin"}</c>. Internal for testing.</summary>
    internal static (string? CgiBase, string? Error) ParseNodeResponse(string json, int statusCode, string hosterName)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("url", out JsonElement url)
                && url.ValueKind == JsonValueKind.String
                && url.GetString() is { Length: > 0 } value)
            {
                return (value.TrimEnd('/'), null);
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return (null, $"{hosterName} returned no upload node (HTTP {statusCode}): {Snippet(json)}");
    }

    /// <summary>Each chunk answers <c>{"status":"OK"}</c>. Internal for testing.</summary>
    internal static string? ValidateChunkResponse(HttpResponseSnapshot response, int index, string hosterName)
    {
        if (response.StatusCode is not (>= 200 and < 300))
        {
            return $"{hosterName} rejected chunk {index.ToString(CultureInfo.InvariantCulture)} (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        return response.Body.Contains("\"OK\"", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{hosterName} rejected chunk {index.ToString(CultureInfo.InvariantCulture)}: {Snippet(response.Body)}";
    }

    /// <summary>
    /// True when a chunk's answer is the NODE being broken rather than the file being refused.
    /// <c>/server</c> hands out a rotating node and some of them are simply down — measured on
    /// DailyUploads, where <c>dn12</c> answered every PUT with a 500 while <c>cdn89</c> and
    /// <c>cdn183</c> took the same bytes happily. Internal for testing.
    /// </summary>
    internal static bool IsNodeUnavailable(HttpResponseSnapshot response) => response.StatusCode >= 500;

    /// <summary>
    /// The finalise fields, verbatim from the captures. <c>sess_id</c> empty is the anonymous signal;
    /// the trailing empties are what the family's own JS sends. Internal for testing.
    /// </summary>
    internal static Dictionary<string, string> BuildFinaliseFields(string sid, string fileName, string sessionId) => new(StringComparer.Ordinal)
    {
        ["op"] = "import_file",
        ["sid"] = sid,
        ["fname"] = fileName,
        ["sess_id"] = sessionId,
        ["file_descr"] = string.Empty,
        ["file_public"] = "0",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
    };

    /// <summary>Pulls <c>links.delete_link</c> out of a finalise reply, when the host sends one.
    /// Internal for testing.</summary>
    internal static string? ParseDeleteLink(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("links", out JsonElement links)
                   && links.ValueKind == JsonValueKind.Object
                   && links.TryGetProperty("delete_link", out JsonElement del)
                   && del.ValueKind == JsonValueKind.String
                   && del.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the share link out of a finalise reply. Hosts on this protocol answer in two shapes and
    /// both are handled: FILEAXA returns a full <c>links.download_link</c> (preferred — the server is
    /// the authority on its own URL form), while DailyUploads returns only <c>file_code</c>, from
    /// which the link is <c>&lt;host&gt;/&lt;code&gt;</c>. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseFinaliseResponse(HttpResponseSnapshot response, string host, string hosterName)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("links", out JsonElement links)
                    && links.ValueKind == JsonValueKind.Object
                    && links.TryGetProperty("download_link", out JsonElement link)
                    && link.ValueKind == JsonValueKind.String
                    && link.GetString() is { Length: > 0 } url)
                {
                    return (url, null);
                }

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
                {
                    return (null, $"{hosterName} refused the file: " + Snippet(error.ToString()));
                }

                if (root.TryGetProperty("file_code", out JsonElement code)
                    && code.ValueKind == JsonValueKind.String
                    && code.GetString() is { Length: > 0 } fileCode)
                {
                    return (host + "/" + fileCode, null);
                }
            }
        }
        catch (JsonException)
        {
            return (null, $"{hosterName} returned an unreadable finalise response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return (null, $"{hosterName} finalise returned no link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
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

    private Dictionary<string, string> NodeHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
    };

    private async Task<(string? CgiBase, string? Error)> GetNodeAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = _getOverride is not null
                ? await _getOverride(ServerUrl)
                : await ctx.Handler.GetSnapshotAsync(ServerUrl, NodeHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{Name} node lookup failed: " + ex.Message);
        }

        return ParseNodeResponse(snap.Body, snap.StatusCode, Name);
    }

    /// <summary>
    /// Runs the upload, retrying ONCE against a freshly looked-up node when the first one turns out
    /// to be down. <c>/server</c> rotates and some nodes are dead (see <see cref="IsNodeUnavailable"/>),
    /// so a single bad draw would otherwise fail the file outright.
    /// <para>
    /// The retry restarts from chunk 0 under a NEW SID, because the server accumulates chunks by SID
    /// on the node that received them — there is nothing on the fresh node to resume. That is only
    /// affordable because nothing exists server-side until the finalise, and it is bounded to one
    /// extra attempt.
    /// </para>
    /// </summary>
    private async Task<(string? Url, string? Error)> UploadAndFinaliseAsync(AttemptContext ctx, string cgiBase)
    {
        string current = cgiBase;

        for (int attempt = 0; ; attempt++)
        {
            string sid = string.Create(CultureInfo.InvariantCulture, $"{Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999)}");
            (string? url, string? error, bool nodeDown) = await AttemptAsync(ctx, current, sid);

            if (error is null)
            {
                return (url, null);
            }

            if (!nodeDown || attempt >= 1)
            {
                return (null, error);
            }

            ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node is down ({current}); retrying once against a fresh one.");

            (string? fresh, string? _) = await GetNodeAsync(ctx);
            if (fresh is null || string.Equals(fresh, current, StringComparison.OrdinalIgnoreCase))
            {
                // No different node to move to — report the node's own failure, which is the
                // diagnosis, rather than the lookup's.
                return (null, error);
            }

            current = fresh;
        }
    }

    private async Task<(string? Url, string? Error, bool NodeDown)> AttemptAsync(AttemptContext ctx, string cgiBase, string sid)
    {
        long total = ctx.FileSize;
        int chunks = (int)Math.Max(1, (total + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;
        string chunkUrl = cgiBase + PutChunkPath;

        Dictionary<string, string> chunkHeaders = NodeHeaders();
        chunkHeaders["X-Upload-SID"] = sid;

        FileStream? file = _chunkOverride is null
            ? new FileStream(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true)
            : null;

        try
        {
            for (int i = 0; i < chunks; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();

                long basePos = (long)i * ChunkSizeBytes;
                long len = Math.Min(ChunkSizeBytes, total - basePos);

                HttpResponseSnapshot resp = _chunkOverride is not null
                    ? await _chunkOverride(chunkUrl, basePos, len)
                    : await ctx.Handler.PutChunkAsync(
                        chunkUrl,
                        new ChunkSliceStream(file!, len),
                        len,
                        basePos,
                        total,
                        started,
                        chunkHeaders,
                        ctx.SpeedLimitProvider,
                        ctx.Cancellation);

                if (ValidateChunkResponse(resp, i, Name) is { } chunkError)
                {
                    return (null, chunkError, IsNodeUnavailable(resp));
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

        Dictionary<string, string> fields = BuildFinaliseFields(sid, ctx.FileName, sessionId: string.Empty);

        HttpResponseSnapshot finalise;
        try
        {
            finalise = _finaliseOverride is not null
                ? await _finaliseOverride(cgiBase + ApiPath, fields)
                : await ctx.Handler.PostMultipartAsync(cgiBase + ApiPath, fields, NodeHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{Name} finalise request failed: " + ex.Message, false);
        }

        // The finalise reply is the only place a delete link ever appears, and an anonymous upload has
        // no account to manage it from — so log it rather than dropping it. Not every host sends one.
        if (ParseDeleteLink(finalise.Body) is { } deleteLink)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: delete link for {ctx.FileName} — {deleteLink}");
        }

        (string? url, string? error) = ParseFinaliseResponse(finalise, Host, Name);
        return (url, error, NodeDown: error is not null && IsNodeUnavailable(finalise));
    }
}
