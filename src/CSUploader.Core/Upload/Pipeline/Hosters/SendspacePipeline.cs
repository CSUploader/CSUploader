// <copyright file="SendspacePipeline.cs" company="CSUploader">
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
/// Sendspace (sendspace.com) — anonymous upload, verified end-to-end against the live service on
/// 2026-08-01 with real bytes (upload, resulting link, and the delete link that removed it again).
/// <list type="number">
///   <item><b>Scrape the homepage.</b> It renders a one-shot upload ticket: a rotating node
///   (<c>fsNNu.sendspace.com/upload?…&amp;UPLOAD_IDENTIFIER=…&amp;DESTINATION_DIR=…&amp;signature=…</c>)
///   plus matching <c>signature</c> and <c>PROGRESS_URL</c> hidden inputs. All of it rotates per
///   page load, so this runs per upload.</item>
///   <item><b>POST the form</b> to that node — the file under <c>upload_file[]</c> alongside the
///   ticket fields. The reply is the result page itself (HTTP 200, no redirect), carrying both
///   <c>sendspace.com/file/&lt;code&gt;</c> and <c>sendspace.com/delete/&lt;code&gt;/&lt;hash&gt;</c>.</item>
/// </list>
/// <para>
/// <b>Ignore the drag-and-drop path.</b> The homepage JS also implements a two-phase XHR upload
/// (<c>/dragupload</c> with <c>fileField</c> + <c>X-File-*</c> headers, returning a hash that is
/// then posted back through this same form). It is only for files dropped onto the page; a file
/// chosen through Browse submits the form directly, which is what a real capture shows and what
/// this reproduces. The <c>/dragupload</c> endpoint refuses everything sent to it from outside a
/// browser session — there is no reason to prefer it.
/// </para>
/// <para>
/// <b>No cookies.</b> The browser sends none to the node: the <c>signature</c> in the ticket is the
/// whole authorisation, so nothing has to be carried over from the homepage fetch but the ticket.
/// </para>
/// <para>
/// <b>300 MB, and it is a real published figure</b> — the page declares
/// <c>upload_form_max_upload_size = 314572800</c> and says "It must be under 300MB. Please upgrade
/// to a Premium account if you need to upload larger files." The scraped value is preferred over
/// the constant when they disagree, so a change on their side surfaces as an accurate refusal
/// rather than a wasted transfer.
/// </para>
/// <para>
/// <b>Failure arrives as a redirect</b> to <c>/uploadprocerr.html?e=N</c> — the only signal the node
/// gives. Its own page blames prohibited file types, antivirus interference or a file in use; in
/// practice it is also what an ill-formed multipart body earns, so the code is surfaced as-is
/// rather than translated into a guess.
/// </para>
/// </summary>
public sealed class SendspacePipeline : IFileHosterPipeline
{
    private const string Host = "https://www.sendspace.com";
    private const string HomeUrl = Host + "/";

    /// <summary>The site's own <c>upload_form_max_upload_size</c>.</summary>
    private const long PublishedMaxFileSize = 314_572_800;

    private const string FileFieldName = "upload_file[]";

    private static readonly Regex FormActionRegex = new(
        @"aria-label=""Upload files""[^>]*action=""(?<url>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SignatureRegex = new(
        @"name=""signature""\s+value=""(?<v>[^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressUrlRegex = new(
        @"name=""PROGRESS_URL""\s+value=""(?<v>[^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MaxSizeRegex = new(
        @"upload_form_max_upload_size\s*=\s*(?<n>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FileLinkRegex = new(
        @"https://www\.sendspace\.com/file/(?<code>[a-z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorRedirectRegex = new(
        @"uploadprocerr\.html\?e=(?<code>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public SendspacePipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage scrape and the upload from canned responses.</summary>
    internal SendspacePipeline(
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "Sendspace";

    /// <summary>Downloads are captcha-free: the file page hands a cookie-less client a server-
    /// rendered direct download link with no captcha markup (live probe, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own FAQ (read 2026-08-12): "A file becomes inactive if it has not been
    /// downloaded at least once during a 30 day period", and "We do not delete active files" -
    /// guest and free alike. Premium files "will not be deleted even if inactive", but that holds
    /// only while the membership does - not a fixed span, so premium reports unknown.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => !credentials.IsAnonymous && credentials.AccountType == AccountType.Premium
            ? FileRetention.Unspecified
            : FileRetention.DaysAfterLastDownload(30);

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>300 MB — the site's own declared figure, so oversized files are skipped at queue
    /// time rather than sent and refused.</summary>
    public long? MaxFileSize => PublishedMaxFileSize;

    public int? MaxFilesPerPackage => null;

    /// <summary>No login, no captcha — the homepage ticket is the whole credential.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: pick up this upload's ticket (it rotates per page load) ===
        (UploadTicket? ticket, string? ticketError) = await GetTicketAsync(ctx);
        if (ticket is null)
        {
            yield return new AttemptFailed(ticketError!, null);
            yield break;
        }

        // The page states the cap it will enforce; believing it costs nothing and turns a doomed
        // transfer into an instant, accurate refusal with the host's own number.
        if (ticket.Value.MaxFileSize is long cap && ctx.FileSize > cap)
        {
            yield return new AttemptFailed(
                // Binary, because the site's "300MB" is 300 MiB exactly (314572800 = 300 × 1024²);
                // rendering it decimally would report the cap as 314.57 MB and read like a typo.
                $"{ctx.FileName} is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}; "
                + $"Sendspace's anonymous limit is {ByteUnit.FromBytes(cap, ByteBase.Binary).ToFriendlyString()}.",
                null);
            yield break;
        }

        // === Step 2: send the bytes ===
        // Looped so a node that is simply down can be retried once against a freshly scraped ticket
        // — which is a DIFFERENT node, since the homepage hands out a rotating one. One
        // TransferStarted covers the whole thing: the retry is our business, not the user's.
        UploadTicket current = ticket.Value;
        bool retriedNodeFailure = false;

        yield return new TransferStarted(ctx.FileSize);

        while (true)
        {
            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, current);

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

            // A transport fault propagates raw to AttemptRunner, which re-runs this pipeline and
            // scrapes a FRESH ticket — the ticket is single-use, so a retry could not reuse this one
            // anyway, and nothing exists server-side until the node answers.
            HttpResponseSnapshot response = await uploadTask;

            (string? url, string? error) = ParseUploadResponse(response);
            if (error is not null)
            {
                if (retriedNodeFailure || !IsNodeUnavailable(response))
                {
                    yield return new AttemptFailed(error, null);
                    yield break;
                }

                retriedNodeFailure = true;
                ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node returned HTTP {response.StatusCode}; retrying once against a fresh node.");

                // Re-scrape: the homepage is what assigns the node, so this is what moves the retry
                // off the one that is down rather than repeating against it.
                (UploadTicket? retryTicket, string? retryError) = await GetTicketAsync(ctx);
                if (retryTicket is null)
                {
                    // Report the node's own failure — the reason we were retrying — rather than the
                    // re-scrape's, which is a symptom of it.
                    _ = retryError;
                    yield return new AttemptFailed(error, null);
                    yield break;
                }

                current = retryTicket.Value;
                continue;
            }

            // The result page is the ONLY place the delete link ever appears — it is not on the
            // file's own page, and an anonymous upload has no account behind it to manage. Log it,
            // because discarding it means the upload can never be taken down.
            if (ParseDeleteLink(response.Body) is { } deleteLink)
            {
                ctx.Logger.Log(this, LogType.Status, $"{Name}: delete link for {ctx.FileName} — {deleteLink}");
            }

            yield return new TransferCompleted(url!);
            yield break;
        }
    }

    /// <summary>
    /// Sendspace accounts aren't wired up — uploads use the anonymous path. Say so plainly rather
    /// than failing silently if someone adds one in Settings. (An account raises the per-file cap
    /// and would let uploads be managed afterwards; it signs in through a plain form at
    /// <c>/login.html</c>.)
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
            "Sendspace login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// One upload's worth of scraped homepage: the node to post to, the two fields that authorise it,
    /// and the cap the page says it will enforce. Every part rotates per page load.
    /// </summary>
    internal readonly record struct UploadTicket(string ActionUrl, string Signature, string ProgressUrl, long? MaxFileSize);

    /// <summary>
    /// Pulls the ticket out of the homepage. <c>PROGRESS_URL</c> is optional — it only drives the
    /// site's own progress bar — but the action and signature are not: without them there is nothing
    /// to post to. Internal for testing.
    /// </summary>
    internal static (UploadTicket? Ticket, string? Error) ParseHomepage(string html, int statusCode)
    {
        Match action = FormActionRegex.Match(html);
        Match signature = SignatureRegex.Match(html);

        if (!action.Success || !signature.Success)
        {
            return (null, $"Sendspace's homepage carried no upload form (HTTP {statusCode}).");
        }

        Match progress = ProgressUrlRegex.Match(html);
        Match max = MaxSizeRegex.Match(html);

        return (new UploadTicket(
            Decode(action.Groups["url"].Value),
            signature.Groups["v"].Value,
            progress.Success ? Decode(progress.Groups["v"].Value) : string.Empty,
            max.Success && long.TryParse(max.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null), null);
    }

    /// <summary>
    /// Success is the result page itself: HTTP 200 whose body carries
    /// <c>sendspace.com/file/&lt;code&gt;</c>. Failure is a redirect to
    /// <c>/uploadprocerr.html?e=N</c> — the node's only diagnostic. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        string body = response.Body ?? string.Empty;

        Match link = FileLinkRegex.Match(body);
        if (link.Success)
        {
            return (link.Value, null);
        }

        Match err = ErrorRedirectRegex.Match(response.LocationHeader ?? string.Empty);
        if (!err.Success)
        {
            err = ErrorRedirectRegex.Match(body);
        }

        if (err.Success)
        {
            return (null,
                    $"Sendspace refused the upload (error {err.Groups["code"].Value}). Its own page blames a "
                    + "prohibited file type, antivirus interference, or the file being open in another program.");
        }

        // A dead node answers with nginx's own error page. Say what happened instead of pasting 500
        // bytes of "a padding to disable MSIE and Chrome friendly error page" into the failure.
        if (IsNodeUnavailable(response))
        {
            return (null, $"Sendspace's upload node is unavailable (HTTP {response.StatusCode}).");
        }

        return (null, $"Sendspace returned no link (HTTP {response.StatusCode}): {Snippet(body)}");
    }

    /// <summary>
    /// True when the answer is the NODE being down rather than a verdict on the file: nginx's own
    /// 5xx page, served before the upload handler ever runs. Observed live as a bare
    /// <c>503 Service Temporarily Unavailable</c> from <c>fs03u</c> in the middle of a batch, while
    /// other files in the same batch went through — the homepage assigns a rotating node, and any
    /// one of them can be out.
    /// <para>
    /// Deliberately narrow: <c>/uploadprocerr.html?e=N</c> is checked first and is a verdict on the
    /// FILE (prohibited type, file in use), which re-sending would only earn again at the cost of
    /// the whole transfer.
    /// </para>
    /// Internal for testing.
    /// </summary>
    internal static bool IsNodeUnavailable(HttpResponseSnapshot response)
        => response.StatusCode is 500 or 502 or 503 or 504;

    /// <summary>The result page also carries a delete link. It isn't used — nothing in the app deletes
    /// an upload — but it is the only way an anonymous upload can ever be removed, so it is worth
    /// pulling out for anyone who needs it. Internal for testing.</summary>
    internal static string? ParseDeleteLink(string body)
    {
        Match m = Regex.Match(body ?? string.Empty, @"https://www\.sendspace\.com/delete/[a-z0-9]+/[a-f0-9]+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    private static string Decode(string value) => System.Net.WebUtility.HtmlDecode(value);

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

    /// <summary>
    /// What the browser sends on the form navigation. No Cookie: the node is given none, and the
    /// signature in the ticket is the whole authorisation.
    /// </summary>
    private static Dictionary<string, string> UploadHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Origin"] = Host,
        ["Referer"] = HomeUrl,
        ["Upgrade-Insecure-Requests"] = "1",
        ["Sec-Fetch-Dest"] = "document",
        ["Sec-Fetch-Mode"] = "navigate",
        ["Sec-Fetch-Site"] = "same-site",
        ["Sec-Fetch-User"] = "?1",
    };

    /// <summary>
    /// The form's own fields. <c>js_enabled=1</c> and the empty <c>file[]</c> are what the browser
    /// posts even when the file was chosen through Browse rather than dropped; the e-mail fields are
    /// the optional "send the link to someone" feature and go empty.
    /// </summary>
    private static Dictionary<string, string> BuildFields(UploadTicket ticket) => new(StringComparer.Ordinal)
    {
        ["PROGRESS_URL"] = ticket.ProgressUrl,
        ["js_enabled"] = "1",
        ["signature"] = ticket.Signature,
        ["upload_files"] = string.Empty,
        ["terms"] = "1",
        ["file[]"] = string.Empty,
        ["description[]"] = string.Empty,
        ["recpemail"] = string.Empty,
        ["ownemail"] = string.Empty,
    };

    private async Task<(UploadTicket? Ticket, string? Error)> GetTicketAsync(AttemptContext ctx)
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
            return (null, "Sendspace homepage fetch failed: " + ex.Message);
        }

        return ParseHomepage(snap.Body, snap.StatusCode);
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, UploadTicket ticket)
    {
        Dictionary<string, string> fields = BuildFields(ticket);

        return _uploadOverride is not null
            ? await _uploadOverride(ctx.FilePath, ticket.ActionUrl, fields, UploadHeaders(), ctx.SpeedBudget)
            : await ctx.Handler.UploadMultipartAsync(
                ctx.FilePath,
                ticket.ActionUrl,
                fileFieldName: FileFieldName,
                extraFields: fields,
                headers: UploadHeaders(),
                speedBudget: ctx.SpeedBudget,
                cancellationToken: ctx.Cancellation);
    }
}
