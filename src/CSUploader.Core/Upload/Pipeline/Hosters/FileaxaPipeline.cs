// <copyright file="FileaxaPipeline.cs" company="CSUploader">
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
/// FILEAXA (fileaxa.com) — <b>anonymous</b> upload on the XFileSharing "xfspro" chunked plugin, the
/// same family <see cref="FilehosterIoPipeline"/> implements. Built from a browser capture of both an
/// anonymous and a signed-in upload, 2026-08-02.
/// <list type="number">
///   <item><b>Node.</b> <c>GET /server</c> → <c>{"url":"https://sNN.fileaxa.com/cgi-bin"}</c>. No key,
///   no cookie, no <c>op=start_upload</c> — simpler than filehoster.io's variant.</item>
///   <item><b>Chunks.</b> PUT each ≤100 MiB slice to <c>&lt;url&gt;/put_chunk.cgi</c> as a raw
///   octet-stream carrying an <c>X-Upload-SID</c> header (a client-minted 16-digit id); each replies
///   <c>{"status":"OK"}</c> and the server appends by SID.</item>
///   <item><b>Finalise.</b> POST <c>&lt;url&gt;/api.cgi</c> as <b>multipart</b> with
///   <c>op=import_file</c>, <c>sid</c>, <c>fname</c>, <c>sess_id</c> and four empty fields →
///   <c>{"status":"OK","file_code":"…","links":{"download_link":"…"}}</c>.</item>
/// </list>
/// <para>
/// <b>Anonymity is one empty field.</b> The captured anonymous and signed-in uploads are identical
/// except for <c>sess_id</c>: empty versus the account's <c>xfss</c> session. Both returned a working
/// link. Same shape as VikingFile's empty <c>user</c> and Webshare's empty <c>wst</c>.
/// </para>
/// <para>
/// <b>This corrects two earlier mistakes about this host, both from reading its homepage instead of
/// watching it work.</b> It was first shipped as an account-only shim on
/// <see cref="XFileSharingApiPipeline"/>: the REST API does exist (a bogus key gets the family's
/// <c>{"status":400,"msg":"Invalid key"}</c>) but the site never uses it, so that upload path was
/// never verified — and "no <c>utype=anon</c> form on the homepage" turned out to mean only that the
/// anonymous uploader is JS-driven, not that anonymous upload was unavailable.
/// </para>
/// <para>
/// <b>Accounts are not wired up yet.</b> The account path needs nothing more than a real
/// <c>sess_id</c> (the <c>xfss</c> cookie from a standard XFileSharing login), but obtaining one
/// means the login machinery that lives in <see cref="XFileSharingApiPipeline"/> and this pipeline is
/// standalone. Anonymous needs none of it. Adding it later would also raise the per-file cap.
/// </para>
/// <para>
/// <b>Second xfspro implementation in the tree</b> (after filehoster.io), and the variants differ in
/// exactly two places: discovery (<c>GET /server</c> here vs <c>POST op=start_upload</c> there) and
/// the finalise encoding (multipart here, form-urlencoded there — Uploadrar's web UI is a third
/// combination). Worth extracting a shared base when a host needs the third; parameterising those two
/// axes is the whole job.
/// </para>
/// </summary>
public sealed class FileaxaPipeline : IFileHosterPipeline
{
    private const string Host = "https://fileaxa.com";
    private const string ServerUrl = Host + "/server";
    private const string PutChunkPath = "/put_chunk.cgi";
    private const string ApiPath = "/api.cgi";

    /// <summary>Matches filehoster.io's: the Cloudflare request-body ceiling the family's JS targets.</summary>
    private const long ChunkSizeBytes = 100L * 1024 * 1024;

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _finaliseOverride;
    private readonly Func<string, long, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public FileaxaPipeline()
    {
    }

    /// <summary>Test ctor — drives node lookup, each chunk and the finalise from canned responses.</summary>
    internal FileaxaPipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, long, long, Task<HttpResponseSnapshot>> chunkOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> finaliseOverride)
    {
        _getOverride = getOverride;
        _chunkOverride = chunkOverride;
        _finaliseOverride = finaliseOverride;
    }

    public string Name => "FILEAXA";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No cap this code can read: the site publishes none where a client can fetch it
    /// (<c>?op=api_get_limits</c> answers with the homepage), and the candidate list's "10000 MB" is
    /// unverified. A guessed cap would reject files the server would take.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous upload is the shipped path — <c>sess_id</c> simply goes out empty.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: which storage node ===
        (string? cgiBase, string? nodeError) = await GetNodeAsync(ctx);
        if (cgiBase is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        // === Step 2: push the bytes under a client-minted SID ===
        // 16 digits, matching the family's JS. Nothing exists server-side until import_file, so a
        // retry that mints a fresh SID orphans the partial upload rather than double-creating.
        string sid = string.Create(CultureInfo.InvariantCulture, $"{Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999)}");

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Url, string? Error)> workTask = UploadAndFinaliseAsync(ctx, cgiBase, sid);

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
    /// FILEAXA accounts aren't wired up — uploads take the anonymous path. Say so plainly rather than
    /// failing silently if someone adds one in Settings. (All an account changes on the wire is a
    /// non-empty <c>sess_id</c>; obtaining it needs the XFileSharing login flow this standalone
    /// pipeline doesn't carry.)
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
            "FILEAXA login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Reads <c>{"url":"https://sNN.fileaxa.com/cgi-bin"}</c>. Internal for testing.</summary>
    internal static (string? CgiBase, string? Error) ParseNodeResponse(string json, int statusCode)
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

        return (null, $"FILEAXA returned no upload node (HTTP {statusCode}): {Snippet(json)}");
    }

    /// <summary>Each chunk answers <c>{"status":"OK"}</c>. Internal for testing.</summary>
    internal static string? ValidateChunkResponse(HttpResponseSnapshot response, int index)
    {
        if (response.StatusCode is not (>= 200 and < 300))
        {
            return $"FILEAXA rejected chunk {index.ToString(CultureInfo.InvariantCulture)} (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        return response.Body.Contains("\"OK\"", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"FILEAXA rejected chunk {index.ToString(CultureInfo.InvariantCulture)}: {Snippet(response.Body)}";
    }

    /// <summary>
    /// Success is <c>{"status":"OK","file_code":…,"links":{"download_link":…}}</c>. The link is taken
    /// from <c>links.download_link</c> rather than rebuilt from <c>file_code</c> — the server is the
    /// authority on its own URL shape. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseFinaliseResponse(HttpResponseSnapshot response)
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
                    return (null, "FILEAXA refused the file: " + Snippet(error.ToString()));
                }
            }
        }
        catch (JsonException)
        {
            return (null, $"FILEAXA returned an unreadable finalise response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return (null, $"FILEAXA finalise returned no link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
    }

    /// <summary>
    /// The finalise fields, verbatim from the capture. <c>sess_id</c> empty is the anonymous signal;
    /// the four trailing fields are sent empty by the site's own JS and are kept for parity.
    /// Internal for testing.
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

    private static Dictionary<string, string> NodeHeaders() => new(StringComparer.OrdinalIgnoreCase)
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
            return (null, "FILEAXA node lookup failed: " + ex.Message);
        }

        return ParseNodeResponse(snap.Body, snap.StatusCode);
    }

    private async Task<(string? Url, string? Error)> UploadAndFinaliseAsync(AttemptContext ctx, string cgiBase, string sid)
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

                if (ValidateChunkResponse(resp, i) is { } chunkError)
                {
                    return (null, chunkError);
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

        // Anonymous: sess_id empty. A future account path supplies the xfss session here and nothing
        // else about the request changes — that is the whole difference in the capture.
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
            return (null, "FILEAXA finalise request failed: " + ex.Message);
        }

        // The finalise reply is the only place the delete link ever appears, and an anonymous upload
        // has no account behind it to manage — dropping it means the upload can never be taken down.
        // Same reasoning as Sendspace's.
        if (ParseDeleteLink(finalise.Body) is { } deleteLink)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: delete link for {ctx.FileName} — {deleteLink}");
        }

        return ParseFinaliseResponse(finalise);
    }

    /// <summary>Pulls <c>links.delete_link</c> out of the finalise reply. Internal for testing.</summary>
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
}
