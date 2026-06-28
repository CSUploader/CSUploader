// <copyright file="GigaPetaPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// GigaPeta (gigapeta.com) upload pipeline — anonymous (not-logged-in) uploads only for now.
/// Verified against the live site 2026-06-12.
/// <list type="bullet">
///   <item><b>HTTP, never HTTPS.</b> gigapeta.com serves a self-signed, expired, weak-key
///   certificate. Windows SChannel rejects it at the TLS handshake (<c>SEC_E_INVALID_TOKEN</c>)
///   and <c>ServerCertificateCustomValidationCallback</c> can't bypass it (the callback runs
///   <em>after</em> a handshake that never completes). Since .NET-on-Windows uses SChannel,
///   every URL here is <c>http://</c>. GigaPeta's own download links are HTTP too.</item>
///   <item><b>Per-upload server assignment.</b> GET <c>http://gigapeta.com/</c> returns an
///   <c>&lt;form id="upload-form" action="http://gNN.upload.gigapeta.com:81/diskNN"&gt;</c> whose
///   upload host + disk rotate on every homepage load — so the form must be scraped fresh for
///   each file, never cached.</item>
///   <item><b>The POST is a browser-shaped multipart</b> to the scraped action plus a random
///   <c>?X-Progress-ID=</c> query (nginx upload-progress key; cosmetic). Fields: <c>MAX_FILE_SIZE</c>,
///   <c>adv_sess</c> (empty for anonymous — populated only by a logged-in session), <c>redom</c>,
///   and the file as <c>file_0</c>. The homepage's <c>Set-Cookie</c>s (<c>auth_token3</c> etc.,
///   scoped to <c>.gigapeta.com</c>) are echoed back on the POST.</item>
///   <item><b>Never send <c>Expect: 100-continue</c>.</b> The upload nodes (<c>nginx/1.2.3</c>)
///   reply 403 if it's present. .NET's <see cref="System.Net.Http.HttpClient"/> doesn't add it by
///   default, so <see cref="HttpHandler.UploadMultipartAsync"/> is already safe — but don't ever
///   flip <c>ExpectContinue</c> on for this host.</item>
///   <item><b>Result.</b> Success is a <c>302</c> whose <c>Location</c> is
///   <c>http://gigapeta.com/dl/{id}?done</c>; stripping the query yields the shareable link
///   (the body also echoes the clean URL as a fallback).</item>
/// </list>
/// No login, no auth cache, no hashing. The <c>adv_sess</c>/login path is intentionally stubbed
/// (see <see cref="CheckAccountAsync"/>) until a real account is available to verify it.
/// </summary>
public sealed class GigaPetaPipeline : IFileHosterPipeline
{
    private const string Host = "http://gigapeta.com";
    private const string HomeUrl = Host + "/";

    /// <summary>Anonymous per-file cap declared by the live upload form's hidden
    /// <c>MAX_FILE_SIZE</c> (250 MiB). The marketing page claims 2 GB "generally", but the
    /// anonymous form is what the server actually enforces. Registered uploads (not yet
    /// supported) lift this to 2 GB.</summary>
    private const long AnonymousMaxFileSizeBytes = 262144000L;

    // The upload form's action points at the rotating upload host (gNN.upload.gigapeta.com).
    // Anchoring on that host keeps us from matching the page's login form, and is robust to
    // attribute ordering (id-before-action vs action-before-id across template versions).
    private static readonly Regex _uploadActionRegex = new(
        """action=["']([^"']*upload\.gigapeta\.com[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _maxFileSizeRegex = new(
        """name=["']MAX_FILE_SIZE["'][^>]*?value=["'](\d+)["']|value=["'](\d+)["'][^>]*?name=["']MAX_FILE_SIZE["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback link extractor: the upload response body echoes the clean download URL even
    // when (for whatever reason) the Location header is absent.
    private static readonly Regex _downloadLinkRegex = new(
        """https?://(?:www\.)?gigapeta\.com/dl/[0-9a-zA-Z]+""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public GigaPetaPipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET and the multipart upload from canned
    /// responses so the scrape/parse logic can be exercised without the network.</summary>
    internal GigaPetaPipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _uploadOverride = uploadOverride;
    }

    public string Name => "GigaPeta";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>GigaPeta accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: anonymous per-file size cap ===
        // The server enforces MAX_FILE_SIZE itself, but failing fast here avoids pushing
        // bytes that are guaranteed to be rejected.
        if (ctx.FileSize > AnonymousMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds GigaPeta's anonymous {ByteUnit.FromBytes(AnonymousMaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: scrape the rotating upload server + carry the homepage cookies ===
        (string? actionUrl, long maxFileSizeField, string cookieHeader, string? scrapeError) = await FetchUploadFormAsync(ctx);
        if (actionUrl is null)
        {
            yield return new AttemptFailed(scrapeError ?? "GigaPeta upload form not found", null);
            yield break;
        }

        // === Step 2: upload ===
        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel,
        // same pattern as the other pipelines (can't yield from inside the event handler).
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, actionUrl, maxFileSizeField, cookieHeader);

        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // Let any transport fault propagate out of RunAsync to the shared retry layer
        // (AttemptRunner): a connect-phase failure or a mid-send abort arrives as a safe-to-retry
        // UploadBodyTransferException, and re-running this whole pipeline scrapes a FRESH rotating
        // upload node — the right recovery, and it never double-creates because the body never
        // finished. A genuine user cancel surfaces as OperationCanceledException and is classified by
        // AttemptRunner. A SERVER VERDICT does NOT throw (UploadMultipartAsync returns the snapshot),
        // so it still flows through ParseUploadResponse below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// GigaPeta login isn't wired up yet — uploads use the anonymous path. Surface a clear
    /// message rather than a silent failure if someone tries to add a GigaPeta account in
    /// Settings. Replace with a real login round-trip once the credential flow is verified.
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
            "GigaPeta login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// GETs the homepage, scrapes the rotating upload-form action URL and the declared
    /// <c>MAX_FILE_SIZE</c>, and builds a <c>Cookie</c> header from the page's
    /// <c>Set-Cookie</c>s (the upload host shares the <c>.gigapeta.com</c> cookie domain).
    /// </summary>
    private async Task<(string? ActionUrl, long MaxFileSize, string CookieHeader, string? Error)> FetchUploadFormAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx, HomeUrl);
        }
        catch (Exception ex)
        {
            return (null, 0, string.Empty, "GigaPeta homepage fetch failed: " + ex.Message);
        }

        Match action = _uploadActionRegex.Match(snap.Body);
        if (!action.Success)
        {
            return (null, 0, string.Empty, $"GigaPeta homepage did not contain an upload-form action URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        long maxFileSizeField = ParseMaxFileSize(snap.Body) ?? AnonymousMaxFileSizeBytes;
        string cookieHeader = BuildCookieHeader(snap.SetCookies);
        return (action.Groups[1].Value, maxFileSizeField, cookieHeader, null);
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl, long maxFileSizeField, string cookieHeader)
    {
        // nginx upload-progress key — cosmetic (drives the site's progress bar), but the
        // browser appends it so we do too. Value is irrelevant to the upload's success.
        int progressId = Random.Shared.Next(1000, 100000);
        string endpoint = actionUrl + (actionUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "X-Progress-ID=" + progressId.ToString(CultureInfo.InvariantCulture);

        // Field order mirrors the browser form: MAX_FILE_SIZE, adv_sess, redom, then file_0
        // (added last by UploadMultipartAsync). adv_sess is empty for anonymous uploads.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["MAX_FILE_SIZE"] = maxFileSizeField.ToString(CultureInfo.InvariantCulture),
            ["adv_sess"] = string.Empty,
            ["redom"] = "gigapeta.com",
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
        };
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            headers["Cookie"] = cookieHeader;
        }

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, endpoint, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            endpoint,
            fileFieldName: "file_0",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is a 302 to <c>http://gigapeta.com/dl/{id}?done</c> (the handler keeps 3xx
    /// un-followed). Strip the query for the clean share link; fall back to scraping the
    /// body when no Location came back.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.LocationHeader is { } loc && loc.Contains("/dl/", StringComparison.OrdinalIgnoreCase))
        {
            return (StripQuery(loc), null);
        }

        Match bodyMatch = _downloadLinkRegex.Match(response.Body);
        if (bodyMatch.Success)
        {
            return (bodyMatch.Value, null);
        }

        return (null, $"GigaPeta upload did not return a download link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
    }

    private static long? ParseMaxFileSize(string html)
    {
        Match m = _maxFileSizeRegex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string captured = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return long.TryParse(captured, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    /// <summary>
    /// Joins every homepage <c>Set-Cookie</c> into a single <c>Cookie</c> header value
    /// (<c>name=value; name=value</c>). The runner-supplied handler is built without
    /// <c>UseCookies</c>, so the pipeline forwards them by hand — the upload host on
    /// <c>:81</c> shares the <c>.gigapeta.com</c> cookie domain.
    /// </summary>
    private static string BuildCookieHeader(IReadOnlyList<string> setCookies)
    {
        List<string> pairs = [];
        foreach (string raw in setCookies)
        {
            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            string pair = (semi < 0 ? raw : raw[..semi]).Trim();
            if (pair.Length > 0 && pair.Contains('=', StringComparison.Ordinal))
            {
                pairs.Add(pair);
            }
        }

        return string.Join("; ", pairs);
    }

    private static string StripQuery(string url)
    {
        int q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : url[..q];
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
}
