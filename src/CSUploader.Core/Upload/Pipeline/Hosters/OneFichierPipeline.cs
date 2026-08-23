// <copyright file="OneFichierPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// 1Fichier (1fichier.com) — anonymous upload. Verified end-to-end against the live site
/// 2026-07-29, including a real (since-deleted) file, so the shape below is what the server
/// actually did rather than what its markup implies.
/// <list type="number">
///   <item><b>Ask the API for a node.</b> <c>POST /v1/upload/get_upload_server.cgi</c> →
///   <c>{"url":"NODE.1fichier.com","id":"XID"}</c>. This is 1Fichier's own documented endpoint
///   (api.html) and needs NO account — only a JSON content type, without which it answers
///   <c>{"message":"Content-Type not JSON #24"}</c>. The homepage renders the same pair into a
///   <c>&lt;form action="…/upload.cgi?id=XID"&gt;</c>, but the API says it without an HTML scrape.
///   Both halves rotate per call (<c>up2</c>, <c>up3</c>, <c>ru-3</c> … and a fresh 10-character
///   <c>XID</c>), and the XID is what the result page is keyed by, so a node is fetched for EVERY
///   upload and never cached.</item>
///   <item><b>POST the file</b> as a single multipart to that action. The file field is
///   <c>file[]</c> — the site's form is a multi-file picker. The browser also sends <c>dpass</c>
///   (download password), <c>user</c> (send-to-member) and the <c>send_ssl</c> checkbox; the live
///   probe proved a bare <c>file[]</c> is accepted, so nothing else is sent.</item>
///   <item><b>Follow the 302.</b> Success is <c>302</c> with <c>Location: /end.pl?xid=XID</c>,
///   relative to the NODE host. Our handler leaves 3xx un-followed, so that header is read directly.</item>
///   <item><b>Read the link off the result page.</b> GET <c>https://NODE.1fichier.com/end.pl?xid=XID</c>
///   for a table of <c>Download link</c> / <c>Removal link</c>; the share link is
///   <c>https://1fichier.com/?&lt;id&gt;</c>. That page says of itself "within some minutes, this page
///   will not be accessible", so it is fetched immediately after the POST — there is no polling step
///   (the site's own <c>/up.pl</c> poll only drives its progress bar; our handler reports progress
///   from the request body).</item>
/// </list>
/// <para>
/// A node that refuses the upload says why in its own page, and it is quoted back rather than dumped
/// as HTML — see <see cref="TryReadServerMessage"/>. One of those messages,
/// <c>Ne peut ouvrir le fichier destination</c>, is the node failing to open its own storage target
/// (observed once in fourteen parallel uploads), so it earns exactly one retry against a freshly
/// resolved node; every other refusal is final. Anything that goes wrong AFTER the POST succeeds is
/// never retried — the file is already stored, and re-sending would duplicate it.
/// </para>
/// <para>
/// <b>Anonymous is capped at 5 GB</b>, not the 300 GB the service is known for — the homepage states
/// "File size is limited to 300GB for customers, 5GB for guests, 50GB for registered users". Wiring
/// an account later is a worthwhile follow-up purely for the cap: it raises the per-file limit
/// tenfold, to 50 GB.
/// </para>
/// </summary>
public sealed partial class OneFichierPipeline : IFileHosterPipeline
{
    private const string Host = "https://1fichier.com";
    private const string HomeUrl = Host + "/";

    /// <summary>Documented node lookup (api.html). Unauthenticated, but it insists on a JSON content
    /// type — a plain GET without one is refused with <c>"Content-Type not JSON #24"</c>.</summary>
    private const string ApiUploadServerUrl = "https://api.1fichier.com/v1/upload/get_upload_server.cgi";

    /// <summary>
    /// Guest per-file cap — 5 GB, stated by the homepage itself. Read as DECIMAL: the exact byte
    /// boundary behind a "5GB" claim is unstated, and of the two ways to be wrong, early-rejecting the
    /// 5.00–5.37 GB sliver costs nothing while accepting a file the server then refuses would waste the
    /// entire upload. Same reasoning as Send.now's guest figure.
    /// </summary>
    private const long AnonymousMaxFileSizeBytes = 5L * 1000 * 1000 * 1000;

    // The share link on the result page: https://1fichier.com/?<20-char id>. The '?' is part of
    // 1fichier's link format, not a query we should strip.
    private static readonly Regex _downloadLinkRegex = DownloadLinkRegex();

    // 1Fichier states its outcome — success or failure — in the page's first non-empty "bloc2" div.
    private static readonly Regex _blocMessageRegex = BlocMessageRegex();
    private static readonly Regex _tagRegex = TagRegex();
    private static readonly Regex _whitespaceRegex = WhitespaceRegex();

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public OneFichierPipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET, the multipart upload and the result-page GET
    /// from canned responses so the scrape/parse chain runs without the network.</summary>
    internal OneFichierPipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _uploadOverride = uploadOverride;
    }

    public string Name => "1Fichier";

    /// <summary>Free downloads are captcha-gated: its pricing comparison lists "Captcha" for
    /// Guests and "No captcha" for Premium (tarifs.html, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>1Fichier accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: guest per-file cap ===
        // Fail before streaming bytes the server is certain to refuse.
        if (ctx.FileSize > AnonymousMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds 1Fichier's guest {ByteUnit.FromBytes(AnonymousMaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: ask the API for this upload's node + session id ===
        (string? actionUrl, string? lookupError) = await FetchUploadNodeAsync(ctx);
        if (actionUrl is null)
        {
            yield return new AttemptFailed(lookupError ?? "1Fichier upload node lookup failed", null);
            yield break;
        }

        // === Step 2: upload, then read the link off the result page ===
        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress through an unbounded channel (the
        // shared pattern — an event handler can't yield).
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Url, string? Error)> workTask = UploadAndResolveLinkAsync(ctx, actionUrl);

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

        // Transport faults propagate out to AttemptRunner, which re-runs this pipeline — and because
        // the node is scraped per attempt, the retry lands on a FRESH node and session id. Nothing can
        // be double-created: a fault means the body never finished, so no result page exists.
        (string? url, string? error) = await workTask;
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// 1Fichier accounts aren't wired up — uploads use the anonymous path. Say so plainly rather than
    /// failing silently if someone adds a 1Fichier account in Settings.
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
            "1Fichier login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Asks the API for this upload's node + session id and builds the POST target from them.</summary>
    private async Task<(string? ActionUrl, string? Error)> FetchUploadNodeAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await FetchNodeSnapshotAsync(ctx);
        }
        catch (Exception ex)
        {
            return (null, "1Fichier upload-node lookup failed: " + ex.Message);
        }

        return TryReadUploadNode(snap.Body) is { } action
            ? (action, null)
            : (null, $"1Fichier upload-node lookup returned no usable node (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    /// <summary>
    /// Builds <c>https://NODE/upload.cgi?id=XID</c> from the lookup's <c>{"url","id"}</c>. Null for
    /// anything that isn't that shape — a refusal envelope (<c>{"status":"KO"}</c>), an error page,
    /// unparseable JSON. Internal for testing.
    /// </summary>
    internal static string? TryReadUploadNode(string json)
    {
        string? url;
        string? id;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            url = doc.RootElement.TryGetProperty("url", out System.Text.Json.JsonElement u) && u.ValueKind == System.Text.Json.JsonValueKind.String ? u.GetString() : null;
            id = doc.RootElement.TryGetProperty("id", out System.Text.Json.JsonElement i) && i.ValueKind == System.Text.Json.JsonValueKind.String ? i.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        // The node is a bare host name ("up3.1fichier.com"), so it is scheme-less by design; reject
        // anything carrying a scheme or path rather than pasting it into a URL and hoping.
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(id) || url.Contains('/', StringComparison.Ordinal)
            ? null
            : $"https://{url}/upload.cgi?id={id}";
    }

    /// <summary>
    /// Uploads and resolves the share link, retrying ONCE against a freshly resolved node when the
    /// node reports its own failure. The retry re-sends the whole file, so it is deliberately capped
    /// at one and gated on a narrow predicate — see <see cref="IsTransientNodeFailure"/>. Progress
    /// restarts from zero on the retry, but the attempt stays a single transfer as far as the UI is
    /// concerned, since this all runs inside one work task.
    /// </summary>
    private async Task<(string? Url, string? Error)> UploadAndResolveLinkAsync(AttemptContext ctx, string actionUrl)
    {
        for (int attempt = 0; ; attempt++)
        {
            (string? url, string? error, bool transient) = await UploadOnceAsync(ctx, actionUrl);
            if (url is not null)
            {
                return (url, null);
            }

            if (!transient || attempt >= 1)
            {
                return (null, error);
            }

            ctx.Logger.Log(this, LogType.Status, $"1Fichier: upload node reported a backend failure ({error}); retrying once with a fresh node.");

            // A fresh lookup means a fresh node AND a fresh session id — the retry cannot land back
            // on the node that just failed to open its destination file.
            (string? retryAction, string? _) = await FetchUploadNodeAsync(ctx);
            if (retryAction is null)
            {
                // Report the node's own failure — the reason we were retrying — not the lookup's,
                // which is a symptom of it.
                return (null, error);
            }

            actionUrl = retryAction;
        }
    }

    /// <summary>
    /// One POST plus the result-page fetch. The POST's own body is the "please wait" page — the link
    /// only exists on the result page named by the <c>Location</c> header, so the redirect is followed
    /// by hand (the handler leaves 3xx alone).
    /// </summary>
    private async Task<(string? Url, string? Error, bool Transient)> UploadOnceAsync(AttemptContext ctx, string actionUrl)
    {
        HttpResponseSnapshot response = await UploadAsync(ctx, actionUrl);

        // Defensive: if a future template ever inlines the link in the POST response, take it.
        if (_downloadLinkRegex.Match(response.Body) is { Success: true } inlineLink)
        {
            return (inlineLink.Value, null, false);
        }

        if (response.LocationHeader is not { } location || location.Length == 0)
        {
            // No redirect means the node refused the upload, and it says why in its own page — quote
            // that rather than dumping HTML at the user.
            string? message = TryReadServerMessage(response.Body);
            string reason = message is null
                ? $"1Fichier upload did not redirect to a result page (HTTP {response.StatusCode}): {Snippet(response.Body)}"
                : $"1Fichier upload was refused: {message}";
            return (null, reason, IsTransientNodeFailure(message, response.StatusCode));
        }

        // Everything past this point follows a SUCCESSFUL POST — the file is already stored. So none
        // of it is retryable, whatever goes wrong: re-sending would upload the same file twice and
        // leave the user a duplicate they never asked for. These failures lose the link, not the file.
        //
        // Location is relative ("/end.pl?xid=…") and belongs to the NODE, not the apex host.
        if (!Uri.TryCreate(new Uri(actionUrl), location, out Uri? resultPage))
        {
            return (null, $"1Fichier returned an unusable result-page location: {location}", false);
        }

        HttpResponseSnapshot end;
        try
        {
            end = await GetSnapshotAsync(ctx, resultPage.AbsoluteUri);
        }
        catch (Exception ex)
        {
            return (null, "1Fichier result-page fetch failed: " + ex.Message, false);
        }

        Match link = _downloadLinkRegex.Match(end.Body);
        return link.Success
            ? (link.Value, null, false)
            : (null, $"1Fichier result page carried no download link (HTTP {end.StatusCode}): {Snippet(end.Body)}", false);
    }

    /// <summary>
    /// The message 1Fichier prints on its own result/error page. It always lands in the first
    /// <c>&lt;div class="bloc2"&gt;</c> — verified across three live pages: the refusal
    /// ("Pas de fichier trouvé dans l'envoi"), the node failure ("Ne peut ouvrir le fichier
    /// destination") and even the success redirect ("Moved ! Temporary Redirect"). The second
    /// <c>bloc2</c> on the page is an empty layout box, hence "first NON-EMPTY". Messages are French
    /// regardless of the site's UI language. Internal for testing.
    /// </summary>
    internal static string? TryReadServerMessage(string html)
    {
        foreach (Match m in _blocMessageRegex.Matches(html))
        {
            string text = System.Net.WebUtility.HtmlDecode(_tagRegex.Replace(m.Groups[1].Value, " "));
            text = _whitespaceRegex.Replace(text, " ").Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the node's message describes IT failing rather than the upload being refused on its
    /// merits. <c>Ne peut ouvrir le fichier destination</c> ("cannot open the destination file") is
    /// the node failing to open its own storage target — seen once out of fourteen parallel uploads,
    /// so it is a concurrency/storage hiccup on their side and a different node will take the file.
    /// <para>
    /// Deliberately narrow, because the retry re-sends the whole file. In particular <c>Pas de fichier
    /// trouvé dans l'envoi</c> is NOT here: that one means the request we built was wrong (it was our
    /// part-header order — see <c>HttpHandler.AddFilePart</c>), and re-sending a malformed upload just
    /// spends the bytes twice to be told the same thing.
    /// </para>
    /// </summary>
    private static bool IsTransientNodeFailure(string? message, int statusCode)
        => statusCode >= 500
           || (message is not null
               && (message.Contains("fichier destination", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("destination file", StringComparison.OrdinalIgnoreCase)));

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, actionUrl, new Dictionary<string, string>(StringComparer.Ordinal), headers, ctx.SpeedBudget);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            actionUrl,
            fileFieldName: "file[]",
            ctx.SpeedBudget,
            extraFields: new Dictionary<string, string>(StringComparer.Ordinal),
            headers: headers,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>The node lookup. A POST with a JSON body is how the endpoint's own documented cURL
    /// example calls it, and it is also the shape that gets the content type onto the wire — a GET
    /// carries no content, so .NET would drop the Content-Type header and the API would refuse us.</summary>
    private Task<HttpResponseSnapshot> FetchNodeSnapshotAsync(AttemptContext ctx)
        => _getSnapshotOverride is not null
            ? _getSnapshotOverride(ApiUploadServerUrl)
            : ctx.Handler.SendJsonAsync(HttpMethod.Post, ApiUploadServerUrl, "{}", headers: null, ctx.Cancellation);

    private Task<HttpResponseSnapshot> GetSnapshotAsync(AttemptContext ctx, string url)
        => _getSnapshotOverride is not null
            ? _getSnapshotOverride(url)
            : ctx.Handler.GetSnapshotAsync(url, headers: null, ctx.Cancellation);

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

    [GeneratedRegex("""https?://(?:www\.)?1fichier\.com/\?[0-9a-zA-Z]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex DownloadLinkRegex();

    [GeneratedRegex("""<div[^>]*\bclass=["']bloc2["'][^>]*>(.*?)</div>""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, "ja-JP")]
    private static partial Regex BlocMessageRegex();

    [GeneratedRegex("""<[^>]+>""", RegexOptions.Compiled, "ja-JP")]
    private static partial Regex TagRegex();

    [GeneratedRegex("""\s+""", RegexOptions.Compiled, "ja-JP")]
    private static partial Regex WhitespaceRegex();
}
