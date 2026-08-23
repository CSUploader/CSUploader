// <copyright file="WebsharePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml.Linq;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Webshare (webshare.cz) — anonymous upload, verified end-to-end against the live service on
/// 2026-08-01 with real bytes, single-shot AND chunked. The site's own uploader is
/// <b>plupload</b>, and this pipeline reproduces exactly what it sends; the field list and the
/// chunk threading below were read out of the site's own script bundle rather than guessed.
/// <list type="number">
///   <item><b>Find a node.</b> <c>POST /api/upload_url/</c> — no key, no cookie, empty body →
///   <c>&lt;response&gt;&lt;status&gt;OK&lt;/status&gt;&lt;url&gt;https://uploadN.wsup.cz/api/upload/index.php&lt;/url&gt;&lt;/response&gt;</c>.
///   The API speaks XML; only the upload node answers JSON.</item>
///   <item><b>Upload.</b> Multipart POST to that node: the file under <c>file</c>, plus
///   <c>wst</c>, <c>folder</c>, <c>private</c>, <c>adult</c>, <c>total</c>, <c>offset</c> and
///   <c>name</c> → <c>{"ident":"…"}</c>.</item>
/// </list>
/// <para>
/// <b>Anonymity is first-class, not a loophole.</b> The site's uploader sets
/// <c>multipart_params: { wst: app().auth().token() || '' }</c> — the auth token, or an empty
/// string when nobody is signed in. Sending <c>wst=""</c> is the site's own anonymous path (same
/// shape as VikingFile's empty <c>user</c>), and a live upload with it returned a working ident.
/// </para>
/// <para>
/// <b>Files over 1 GiB are chunked</b>, at the site's own <c>chunk_size: '1gb'</c>. The first chunk
/// carries <c>offset=0</c> and NO <c>ident</c>; its response mints one, and every later chunk sends
/// that <c>ident</c> plus the running <c>offset</c>. The server assembles the file when a chunk
/// completes <c>total</c>. Verified live by uploading a file in two halves and confirming the
/// reassembled size through <c>/api/file_info/</c>.
/// </para>
/// <para>
/// <b>The share link is NOT the one the site's own JS builds.</b> Its <c>fileLink()</c> produces
/// <c>webshare.cz/#/file/&lt;ident&gt;/&lt;slug&gt;</c>, a client-side SPA route: fetching it returns
/// the empty app shell, so anything that isn't a browser with JS — a link checker, a forum's
/// preview, another downloader — sees nothing. The same path without the <c>#</c> is served by the
/// server and renders the real file page, so that is what this emits.
/// </para>
/// <para>
/// <b>No declared cap.</b> The uploader's own <c>max_file_size</c> is <c>'200gb'</c>, which is a
/// client-side guard for signed-in users and says nothing about what an anonymous upload may be
/// allowed; the candidate list's "20 GB" has no source either. So <see cref="MaxFileSize"/> stays
/// null and the server's refusal is the authority.
/// </para>
/// <para>
/// Accounts are not wired up. The token in <c>wst</c> comes from <c>/api/salt/</c> +
/// <c>/api/login/</c> (md5-crypt then SHA-1), which is a self-contained addition whenever it is
/// wanted — it would also unlock the file-management calls. Anonymous needs none of it.
/// </para>
/// </summary>
public sealed class WebsharePipeline : IFileHosterPipeline
{
    private const string Host = "https://webshare.cz";
    private const string UploadUrlEndpoint = Host + "/api/upload_url/";

    /// <summary>Server-rendered file page. The site's own JS links the <c>#/</c> variant of this
    /// path, which only a browser can resolve — see the class remarks.</summary>
    private const string FilePageBase = Host + "/file/";

    /// <summary>The site's own <c>chunk_size: '1gb'</c>.</summary>
    private const long ChunkSize = 1L << 30;

    /// <summary>plupload's default <c>file_data_name</c>, which the site does not override.</summary>
    private const string FileFieldName = "file";

    private static readonly Regex NonSlugRegex = new("[^0-9A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, long, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public WebsharePipeline()
    {
    }

    /// <summary>Test ctor — drives the node lookup, the single-shot upload and each chunk from
    /// canned responses, so the ident/offset threading runs without the network.</summary>
    internal WebsharePipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, long, long, Task<HttpResponseSnapshot>>? chunkOverride = null)
    {
        _postFormOverride = postFormOverride;
        _uploadOverride = uploadOverride;
        _chunkOverride = chunkOverride;
    }

    public string Name => "Webshare";

    /// <summary>Downloads are captcha-free: an anonymous file_link API call returns a direct
    /// link that serves the bytes; VIP buys speed, not captcha removal (live probe,
    /// 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>Undeclared for anonymous use — see the class remarks.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>The site's own uploader sends an empty <c>wst</c> when signed out, so anonymous is
    /// the supported path rather than a workaround.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: pick up a storage node (fresh per file — they rotate) ===
        (string? node, string? nodeError) = await GetUploadNodeAsync(ctx);
        if (node is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        // === Step 2: send the bytes ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Ident, string? Error)> workTask = UploadAsync(ctx, node);

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

        // A transport fault propagates raw to AttemptRunner, which re-runs this pipeline from a fresh
        // node lookup with no ident — an abandoned partial upload is never assembled, so a retry
        // cannot double-create.
        (string? ident, string? error) = await workTask;
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(BuildFileLink(ident!, ctx.FileName));
    }

    /// <summary>
    /// Webshare accounts aren't wired up — uploads use the anonymous path. Say so plainly rather than
    /// failing silently if someone adds one in Settings. (The <c>wst</c> token comes from
    /// <c>/api/salt/</c> + <c>/api/login/</c>; adding it would also unlock file management.)
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
            "Webshare login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// Reads <c>&lt;response&gt;&lt;status&gt;OK&lt;/status&gt;&lt;url&gt;…&lt;/url&gt;&lt;/response&gt;</c>,
    /// or the API's own <c>&lt;message&gt;</c> when it refuses. The whole <c>/api/</c> surface is XML —
    /// only the upload node answers JSON. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadUrlResponse(string xml, int statusCode)
    {
        try
        {
            XElement? root = XDocument.Parse(xml).Root;
            if (root is not null)
            {
                string? status = root.Element("status")?.Value;
                string? url = root.Element("url")?.Value;

                if (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                {
                    return (url, null);
                }

                string? message = root.Element("message")?.Value;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return (null, "Webshare refused the upload-node request: " + message);
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // Fall through to the generic message — a non-XML body is a bad gateway, not a refusal.
        }

        return (null, $"Webshare returned no upload node (HTTP {statusCode}): {Snippet(xml)}");
    }

    /// <summary>
    /// The node answers <c>{"jsonrpc":"2.0","result":null,"id":"id","ident":"…"}</c>. A refusal comes
    /// back as an <c>error</c> object whose <c>code</c> indexes the site's own message list — those
    /// read far better than "upload failed", so they are reproduced. Internal for testing.
    /// </summary>
    internal static (string? Ident, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, $"Webshare upload returned an unexpected response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
            }

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.Number)
            {
                int code = error.ValueKind == JsonValueKind.Number
                    ? error.GetInt32()
                    : (error.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0);
                return (null, "Webshare refused the file: " + DescribeErrorCode(code));
            }

            if (root.TryGetProperty("ident", out JsonElement ident)
                && ident.ValueKind == JsonValueKind.String
                && ident.GetString() is { Length: > 0 } value)
            {
                return (value, null);
            }

            return (null, $"Webshare upload returned no ident (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
        catch (JsonException)
        {
            return (null, $"Webshare upload returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
    }

    /// <summary>The site's own error table, in English. Internal for testing.</summary>
    internal static string DescribeErrorCode(int code) => code switch
    {
        1 => "not enough space.",
        2 => "the filename looks like copyrighted content, which Webshare rejects.",
        3 => "the file was flagged as suspicious.",
        4 => "too many identical files have been uploaded.",
        _ => $"the server gave no reason (error code {code.ToString(CultureInfo.InvariantCulture)}).",
    };

    /// <summary>
    /// Rebuilds the site's own slug: diacritics stripped, every run of non-alphanumerics collapsed to
    /// a single hyphen, ends trimmed, lowercased. It is cosmetic — the ident alone identifies the
    /// file — but matching the site keeps our links identical to the ones it shows the user.
    /// Internal for testing.
    /// </summary>
    internal static string Slugify(string name)
    {
        string decomposed = name.Normalize(NormalizationForm.FormD);
        StringBuilder stripped = new(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(ch);
            }
        }

        return NonSlugRegex.Replace(stripped.ToString().Normalize(NormalizationForm.FormC), "-").Trim('-').ToLowerInvariant();
    }

    /// <summary>Internal for testing.</summary>
    internal static string BuildFileLink(string ident, string fileName) => FilePageBase + ident + "/" + Slugify(fileName);

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

    /// <summary>Browser-shaped headers, as the site's own uploader sends them.</summary>
    private static Dictionary<string, string> NodeHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
    };

    /// <summary>
    /// The parameters plupload posts alongside every chunk. <c>wst</c> empty is the anonymous
    /// signal; <c>offset</c> is how far into the file this part starts and <c>total</c> is the whole
    /// size, which is how the server knows when it has everything.
    /// </summary>
    private static Dictionary<string, string> BuildFields(AttemptContext ctx, long offset, string? ident)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["wst"] = string.Empty,
            ["folder"] = "/",
            ["private"] = "0",
            ["adult"] = "0",
            ["total"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["name"] = ctx.FileName,
        };

        // Absent on the first chunk — that request is what mints it.
        if (ident is not null)
        {
            fields["ident"] = ident;
        }

        return fields;
    }

    private async Task<(string? Node, string? Error)> GetUploadNodeAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            Dictionary<string, string> empty = new(StringComparer.Ordinal);
            snap = _postFormOverride is not null
                ? await _postFormOverride(UploadUrlEndpoint, empty)
                : await ctx.Handler.PostFormAsync(UploadUrlEndpoint, empty, NodeHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "Webshare upload-node lookup failed: " + ex.Message);
        }

        return ParseUploadUrlResponse(snap.Body, snap.StatusCode);
    }

    private async Task<(string? Ident, string? Error)> UploadAsync(AttemptContext ctx, string node)
    {
        if (ctx.FileSize <= ChunkSize)
        {
            HttpResponseSnapshot single = _uploadOverride is not null
                ? await _uploadOverride(ctx.FilePath, node, BuildFields(ctx, 0, null), ctx.SpeedBudget)
                : await ctx.Handler.UploadMultipartAsync(
                    ctx.FilePath,
                    node,
                    fileFieldName: FileFieldName,
                    extraFields: BuildFields(ctx, 0, null),
                    headers: NodeHeaders(),
                    speedBudget: ctx.SpeedBudget,
                    cancellationToken: ctx.Cancellation);

            return ParseUploadResponse(single);
        }

        return await UploadChunkedAsync(ctx, node);
    }

    /// <summary>
    /// Sequential chunks, threading the ident the first response mints. The server assembles once a
    /// chunk lands on <c>total</c>, so only the last response matters for success — but an explicit
    /// error on any chunk ends it immediately rather than pushing the remaining gigabytes.
    /// </summary>
    private async Task<(string? Ident, string? Error)> UploadChunkedAsync(AttemptContext ctx, string node)
    {
        long total = ctx.FileSize;
        DateTime started = DateTime.Now;
        string? ident = null;

        FileStream? file = _chunkOverride is null
            ? new FileStream(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true)
            : null;

        try
        {
            for (long offset = 0; offset < total; offset += ChunkSize)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();

                long len = Math.Min(ChunkSize, total - offset);
                Dictionary<string, string> fields = BuildFields(ctx, offset, ident);

                HttpResponseSnapshot resp;
                if (_chunkOverride is not null)
                {
                    resp = await _chunkOverride(node, fields, offset, len);
                }
                else
                {
                    // ChunkSliceStream serves exactly `len` bytes from the shared FileStream (whose
                    // position advances as each slice is consumed) and never disposes it.
                    ChunkSliceStream slice = new(file!, len);
                    resp = await ctx.Handler.PostFileChunkAsync(
                        node,
                        fields,
                        fileFieldName: FileFieldName,
                        fileName: ctx.FileName,
                        chunkData: slice,
                        chunkLength: len,
                        basePosition: offset,
                        totalFileSize: total,
                        dateTimeStarted: started,
                        headers: NodeHeaders(),
                        speedBudget: ctx.SpeedBudget,
                        cancellationToken: ctx.Cancellation);
                }

                (string? chunkIdent, string? chunkError) = ParseUploadResponse(resp);
                if (chunkError is not null)
                {
                    return (null, chunkError);
                }

                // Every chunk echoes the same ident; keeping the first is what ties the rest together.
                ident ??= chunkIdent;
            }
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }

        return ident is null
            ? (null, "Webshare upload finished without an ident.")
            : (ident, null);
    }
}
