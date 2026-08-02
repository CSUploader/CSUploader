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

    /// <summary>
    /// How many times a single lookup will re-draw when <c>/server</c> keeps offering a node that was
    /// recently seen to fail. The rotation is small — DailyUploads served four distinct nodes across
    /// twelve consecutive lookups — so a handful of draws is enough to step past one bad member, and
    /// each draw is a tiny GET.
    /// </summary>
    private const int NodeDrawsPerLookup = 4;

    /// <summary>
    /// Total times the file may be sent, counting the first. Every retry re-sends from byte 0 (see
    /// <see cref="UploadAndFinaliseAsync"/>), so this stays deliberately small.
    /// </summary>
    private const int MaxNodeAttempts = 3;

    /// <summary>
    /// How long a node that answered with a server fault is stepped over. A cooldown rather than a
    /// verdict: the host does fix nodes, and this must not poison a long-running session.
    /// </summary>
    private static readonly TimeSpan DeadNodeCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Nodes seen answering a chunk with 5xx, and when. Keyed by the full cgi-base URL, so the two
    /// hosts on this base cannot collide. An INSTANCE field, and these pipelines are registered as
    /// singletons — which is the point: the first file to draw a dead node is what spares the rest of
    /// the batch from drawing it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _nodeFailedUtc = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Draws a node, stepping over any this pipeline has recently seen fail.
    /// <para>
    /// <c>/server</c> hands back a rotating member of a small pool and a dead one stays dead: measured
    /// on DailyUploads, <c>dn12</c> answered three of three PUTs with 500 while <c>cdn89</c>,
    /// <c>d900</c> and <c>cdn181</c> each took the same megabyte three of three. Twelve consecutive
    /// lookups drew <c>dn12</c> twice. So without this, roughly one upload in six was handed a node
    /// that could not possibly accept it — and, being a singleton, the pipeline was in a position to
    /// know better.
    /// </para>
    /// </summary>
    private async Task<(string? CgiBase, string? Error)> GetNodeAsync(AttemptContext ctx)
    {
        (string? CgiBase, string? Error) drawn = (null, null);

        for (int draw = 0; draw < NodeDrawsPerLookup; draw++)
        {
            drawn = await DrawNodeAsync(ctx);
            if (drawn.CgiBase is null || !RecentlyFailed(drawn.CgiBase))
            {
                return drawn;
            }
        }

        // Every draw came back a node we just saw fail. Send to the last one anyway rather than fail
        // the file on our own bookkeeping — the mark is a cooldown, not a verdict.
        ctx.Logger.Log(this, LogType.Status, $"{Name}: every upload node offered was one that recently failed; trying {drawn.CgiBase} regardless.");
        return drawn;
    }

    private async Task<(string? CgiBase, string? Error)> DrawNodeAsync(AttemptContext ctx)
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

    private bool RecentlyFailed(string cgiBase)
    {
        if (!_nodeFailedUtc.TryGetValue(cgiBase, out DateTime failedUtc))
        {
            return false;
        }

        if (DateTime.UtcNow - failedUtc < DeadNodeCooldown)
        {
            return true;
        }

        _nodeFailedUtc.TryRemove(cgiBase, out _);
        return false;
    }

    /// <summary>
    /// Runs the upload, moving to a freshly drawn node when the one in hand turns out to be down
    /// (see <see cref="IsNodeUnavailable"/>), up to <see cref="MaxNodeAttempts"/> sends in total.
    /// <para>
    /// Each failed node is recorded, which is what makes the retry converge: the next draw steps over
    /// it, and so does every other file in the batch (<see cref="GetNodeAsync"/>).
    /// </para>
    /// <para>
    /// A retry restarts from chunk 0 under a NEW SID, because the server accumulates chunks by SID on
    /// the node that received them — there is nothing on a fresh node to resume, and re-sending under
    /// the old SID could double-append whatever the failed PUT did store. That is affordable only
    /// because nothing exists server-side until the finalise, and it is why the attempt count stays
    /// small: the cost of each retry is the whole file.
    /// </para>
    /// </summary>
    private async Task<(string? Url, string? Error)> UploadAndFinaliseAsync(AttemptContext ctx, string cgiBase)
    {
        string current = cgiBase;

        for (int attempt = 1; ; attempt++)
        {
            string sid = string.Create(CultureInfo.InvariantCulture, $"{Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999)}");
            (string? url, string? error, bool nodeDown) = await AttemptAsync(ctx, current, sid);

            if (error is null)
            {
                return (url, null);
            }

            if (!nodeDown)
            {
                // The file was refused, not the node — a different node would refuse it too.
                return (null, error);
            }

            // Record it even when this file is out of attempts: the rest of the batch still benefits.
            _nodeFailedUtc[current] = DateTime.UtcNow;

            if (attempt >= MaxNodeAttempts)
            {
                return (null, error);
            }

            ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node {current} is down; re-sending to a different one (attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of {MaxNodeAttempts.ToString(CultureInfo.InvariantCulture)}).");

            (string? fresh, string? _) = await GetNodeAsync(ctx);
            if (fresh is null)
            {
                // The lookup itself failed — report the node's fault, which is the diagnosis.
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
