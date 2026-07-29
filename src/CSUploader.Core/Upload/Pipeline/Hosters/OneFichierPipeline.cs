// <copyright file="OneFichierPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
///   <item><b>Scrape the rotating node.</b> GET <c>https://1fichier.com/</c> — plain nginx, no
///   Cloudflare, no cookies — which renders
///   <c>&lt;form id="files" action="https://NODE.1fichier.com/upload.cgi?id=XID"&gt;</c>. BOTH halves
///   rotate per page load (<c>up2</c>, <c>up3</c>, <c>ru-3</c> … and a fresh 10-character
///   <c>XID</c>), and the XID is what the result page is keyed by, so this is scraped for EVERY
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

    /// <summary>
    /// Guest per-file cap — 5 GB, stated by the homepage itself. Read as DECIMAL: the exact byte
    /// boundary behind a "5GB" claim is unstated, and of the two ways to be wrong, early-rejecting the
    /// 5.00–5.37 GB sliver costs nothing while accepting a file the server then refuses would waste the
    /// entire upload. Same reasoning as Send.now's guest figure.
    /// </summary>
    private const long AnonymousMaxFileSizeBytes = 5L * 1000 * 1000 * 1000;

    // The upload form's action is the rotating node. Anchored on upload.cgi so it can only match the
    // uploader, never another form on the page, and captured verbatim because the ?id= query IS the
    // upload session — dropping it would post into nowhere.
    private static readonly Regex _uploadActionRegex = UploadActionRegex();

    // The share link on the result page: https://1fichier.com/?<20-char id>. The '?' is part of
    // 1fichier's link format, not a query we should strip.
    private static readonly Regex _downloadLinkRegex = DownloadLinkRegex();

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public OneFichierPipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET, the multipart upload and the result-page GET
    /// from canned responses so the scrape/parse chain runs without the network.</summary>
    internal OneFichierPipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _uploadOverride = uploadOverride;
    }

    public string Name => "1Fichier";

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

        // === Step 1: scrape this upload's node + session id ===
        (string? actionUrl, string? scrapeError) = await FetchUploadFormAsync(ctx);
        if (actionUrl is null)
        {
            yield return new AttemptFailed(scrapeError ?? "1Fichier upload form not found", null);
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

    /// <summary>GETs the homepage and scrapes this upload's node + session id out of the form action.</summary>
    private async Task<(string? ActionUrl, string? Error)> FetchUploadFormAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx, HomeUrl);
        }
        catch (Exception ex)
        {
            return (null, "1Fichier homepage fetch failed: " + ex.Message);
        }

        Match action = _uploadActionRegex.Match(snap.Body);
        return action.Success
            ? (action.Groups[1].Value, null)
            : (null, $"1Fichier homepage did not contain an upload-form action URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    /// <summary>
    /// POSTs the file, then resolves the share link. The POST's own body is the "please wait" page —
    /// the link only exists on the result page named by the <c>Location</c> header, so the redirect is
    /// followed by hand (the handler leaves 3xx alone).
    /// </summary>
    private async Task<(string? Url, string? Error)> UploadAndResolveLinkAsync(AttemptContext ctx, string actionUrl)
    {
        HttpResponseSnapshot response = await UploadAsync(ctx, actionUrl);

        // Defensive: if a future template ever inlines the link in the POST response, take it.
        if (_downloadLinkRegex.Match(response.Body) is { Success: true } inlineLink)
        {
            return (inlineLink.Value, null);
        }

        if (response.LocationHeader is not { } location || location.Length == 0)
        {
            return (null, $"1Fichier upload did not redirect to a result page (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        // Location is relative ("/end.pl?xid=…") and belongs to the NODE, not the apex host.
        if (!Uri.TryCreate(new Uri(actionUrl), location, out Uri? resultPage))
        {
            return (null, $"1Fichier returned an unusable result-page location: {location}");
        }

        HttpResponseSnapshot end;
        try
        {
            end = await GetSnapshotAsync(ctx, resultPage.AbsoluteUri);
        }
        catch (Exception ex)
        {
            return (null, "1Fichier result-page fetch failed: " + ex.Message);
        }

        Match link = _downloadLinkRegex.Match(end.Body);
        return link.Success
            ? (link.Value, null)
            : (null, $"1Fichier result page carried no download link (HTTP {end.StatusCode}): {Snippet(end.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, actionUrl, new Dictionary<string, string>(StringComparer.Ordinal), headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            actionUrl,
            fileFieldName: "file[]",
            extraFields: new Dictionary<string, string>(StringComparer.Ordinal),
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

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

    [GeneratedRegex("""action=["']([^"']*\.1fichier\.com/upload\.cgi\?[^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex UploadActionRegex();

    [GeneratedRegex("""https?://(?:www\.)?1fichier\.com/\?[0-9a-zA-Z]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex DownloadLinkRegex();
}
