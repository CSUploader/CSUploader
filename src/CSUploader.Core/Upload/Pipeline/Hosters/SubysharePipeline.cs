// <copyright file="SubysharePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// SubyShare (subyshare.com) — XFileSharing of an OLDER vintage: <b>account-only</b>, <b>5 GB</b> per
/// file, and a <b>free account can upload</b>.
/// <para>
/// ⚠ <b>The candidate list had this host down as premium-only, and that is wrong.</b> A free
/// registered account uploaded twice in the supplied capture, and its <c>/upload</c> page offers the
/// file form to exactly that account.
/// </para>
/// <para>
/// The sign-in is stock (<c>op=login</c> POSTed to the site root, no captcha, a 30-day <c>xfss</c>),
/// so only three things about the upload differ from the modern family:
/// </para>
/// <list type="number">
///   <item><b>The form's action is incomplete on purpose.</b> It ends <c>upload.cgi?upload_id=</c>
///   and the page's own script appends the id plus <c>js_on</c>, <c>utype</c>, <c>upload_type</c> and
///   <c>usr_id</c>. Posting the scraped action verbatim — the family default — would send the whole
///   file to a request with no upload id at all.</item>
///   <item><b>The field set is this fork's own:</b> the account's numeric <c>usr_id</c> and the
///   node's <c>srv_tmp_url</c> ride alongside <c>sess_id</c>, and the family's <c>utype</c> is in the
///   QUERY rather than the body.</item>
///   <item><b>The reply is not JSON.</b> It is a self-submitting HTML form —
///   <c>&lt;textarea name='fn'&gt;CODE&lt;/textarea&gt;&lt;textarea name='st'&gt;OK&lt;/textarea&gt;</c>
///   — translated back into the family's envelope by <see cref="NormalizeUploadResponse"/> so the
///   shared parser is still the one deciding what "success" means.</item>
/// </list>
/// </summary>
public sealed class SubysharePipeline : XFileSharingApiPipeline
{
    /// <summary>The upload page's own <c>max_upload_filesize='5120'</c>. Binary, as XFileSharing's
    /// limits are 1024-based.</summary>
    private const long MaxFileSizeBytes = 5120L * 1024 * 1024;

    /// <summary>The account's numeric id, a hidden field on the upload page. It rides BOTH the query
    /// and the multipart, exactly as this fork's own uploader sends it.</summary>
    private static readonly Regex UsrIdRegex = new(
        """name=["']usr_id["'][^>]*value=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Where the node stages a part-finished upload; the page hands it over and the POST
    /// hands it back.</summary>
    private static readonly Regex SrvTmpUrlRegex = new(
        """name=["']srv_tmp_url["'][^>]*value=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The file form's action. ⚠ The page carries a SECOND form with an identical action —
    /// the remote-URL uploader — so this deliberately takes the first, which is the file one. (On
    /// filedot.to the same shape of page cost an upload: there the only action belonged to the URL
    /// uploader.)</summary>
    private static readonly Regex UploadActionRegex = new(
        """<form[^>]*action=["']([^"']*upload\.cgi[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The pre-JSON reply's fields: <c>fn</c> is the file code, <c>st</c> the status.</summary>
    private static readonly Regex ResultFieldRegex = new(
        """<textarea[^>]*name=['"](fn|st)['"][^>]*>([^<]*)</textarea>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The two page-scraped fields, keyed by attempt — NOT plain fields, because a pipeline is a
    /// singleton and two concurrent uploads would otherwise trade values. Written when the upload
    /// page is resolved, read (and removed) when that attempt's field set is built, so the pair is
    /// always the one that came off the page this POST is aimed at. An attempt abandoned between the
    /// two (a cancel) leaves its entry behind, hence the age sweep on write.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, UploadPageFields> _pageFieldsByAttempt = new();

    public SubysharePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal SubysharePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    public override string Name => "SubyShare";

    protected override string Host => "https://subyshare.com";

    /// <summary>Its uploader sits behind the sign-in; no guest form is offered anywhere on the
    /// site.</summary>
    public override bool SupportsAnonymousUpload => false;

    /// <summary>No REST API — the account uploads through the logged-in web form.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>A plain <c>login</c>/<c>password</c> form with no captcha, and posting it answers
    /// <c>302 + Set-Cookie: xfss</c>. So no sign-in window opens. The POST goes to the site root,
    /// which is the family default.</summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>This fork's account routes live under <c>/account/</c>; the family's
    /// <c>/login.html</c> is not where the form is.</summary>
    protected override string LoginPagePath => "/account/login";

    /// <summary>Its uploader is a plain <c>/upload</c> route rather than <c>?op=upload_form</c>.</summary>
    protected override string UploadFormUrl => Host + "/upload";

    /// <summary>Where the signed-in check looks. <c>?op=my_files</c> does still work on this fork (its
    /// own menu links it as "Basic Mode"), but <c>/filemanager</c> is the page the browser actually
    /// loads, so it is the one whose signed-in chrome is known rather than assumed.</summary>
    protected override string WebFormAccountPageUrl => Host + "/filemanager";

    /// <summary>5 GB — the figure this fork's upload page states for a free account.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
    {
        _ = credentials;
        return MaxFileSizeBytes;
    }

    /// <summary>
    /// This fork links <c>/account/logout</c>, not the family's <c>?op=logout</c> — the same quirk
    /// DDownload, DataNodes and PreFiles have, and the same consequence if it is missed: the stock
    /// probe reports a perfectly good session as a failed sign-in. Accepts either.
    /// </summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase)
           || html.Contains("/logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reports no name: this fork's file manager carries none. Its header is a bare nav — no
    /// greeting, no <c>fa-user</c> block — so the family's scrape has nothing to find, and leaving
    /// the base to keep the login name the user typed is both correct and what they would recognise
    /// (it is also what the next sign-in POSTs). Same call as PreFiles, for a different reason.
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        _ = html;
        return null;
    }

    /// <summary>
    /// Builds the URL this fork's own script would build. The scraped action carries an EMPTY
    /// <c>upload_id=</c>; the id, <c>js_on</c>, <c>utype</c>, <c>upload_type</c> and <c>usr_id</c>
    /// are appended client-side. It also banks the two hidden fields the multipart needs, since this
    /// is the one point at which the page is in hand.
    /// </summary>
    protected override Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> ResolveWebFormUploadServerAsync(
        AttemptContext ctx, string uploadFormHtml, string xfss, CancellationToken ct)
    {
        _ = ct;

        Match action = UploadActionRegex.Match(uploadFormHtml);
        if (!action.Success)
        {
            return Task.FromResult<(string?, string?, string?, bool)>(
                (null, null, "upload form not found — the session may have expired", true));
        }

        Match usrId = UsrIdRegex.Match(uploadFormHtml);
        if (!usrId.Success)
        {
            // Signed out, this page is the marketing one and carries no usr_id. Stopping here rather
            // than posting without it matters: XFileSharing decides who a file belongs to from the
            // fields, and a refusal is cheaper than 5 GB filed under nobody.
            return Task.FromResult<(string?, string?, string?, bool)>(
                (null, null, "the upload page carried no usr_id — the session may have expired", true));
        }

        Match srvTmpUrl = SrvTmpUrlRegex.Match(uploadFormHtml);
        SweepAbandonedPageFields();
        _pageFieldsByAttempt[ctx.AttemptId] = new UploadPageFields(
            usrId.Groups[1].Value,
            srvTmpUrl.Success ? srvTmpUrl.Groups[1].Value : string.Empty,
            DateTime.UtcNow);

        string url = action.Groups[1].Value;
        int query = url.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            url = url[..query];
        }

        return Task.FromResult<(string?, string?, string?, bool)>((
            $"{url}?upload_id={NewUploadId()}&js_on=1&utype=reg&upload_type=file&usr_id={usrId.Groups[1].Value}",
            ScrapeSessId(uploadFormHtml, xfss),
            null,
            false));
    }

    /// <summary>
    /// The field set this fork's own form posts: the family's <c>sess_id</c> plus the account's
    /// <c>usr_id</c>, the node's <c>srv_tmp_url</c> and its submit button — and none of the modern
    /// family's <c>file_public</c>/<c>file_descr</c>/<c>keepalive</c>. <c>utype</c> is absent
    /// deliberately: on this fork it travels in the query string instead.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(AttemptContext ctx, string sessId)
    {
        if (!_pageFieldsByAttempt.TryRemove(ctx.AttemptId, out UploadPageFields? page))
        {
            // Unreachable by design — every upload is preceded by the resolve that banks these. Loud
            // rather than silent, because the fallback (post without them) is the case where the
            // node takes the bytes and files them under nobody.
            throw new InvalidOperationException(
                $"{Name}: the upload page's usr_id was not captured for this attempt — refusing to upload unattributed.");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["upload_type"] = "file",
            ["sess_id"] = sessId,
            ["usr_id"] = page.UsrId,
            ["srv_tmp_url"] = page.SrvTmpUrl,
            ["submit_btn"] = "Upload!",
            ["link_pass"] = string.Empty,
            ["to_folder"] = string.Empty,
        };
    }

    /// <summary>
    /// Turns this fork's pre-JSON reply into the family's envelope so the shared parser judges it.
    /// <para>
    /// The node answers with a self-submitting HTML form —
    /// <c>&lt;textarea name='fn'&gt;0b2s3zsxvf16&lt;/textarea&gt;&lt;textarea name='st'&gt;OK&lt;/textarea&gt;</c>.
    /// Translating rather than reading it here is deliberate: the shared parser already knows that a
    /// status of OK with a code of <c>undef</c> means the upload was DISCARDED, and a fork-specific
    /// reader would almost certainly be written without that.
    /// </para>
    /// </summary>
    protected override HttpResponseSnapshot NormalizeUploadResponse(HttpResponseSnapshot response)
    {
        if (response.Body.Contains("file_status", StringComparison.OrdinalIgnoreCase))
        {
            return response;   // already the modern shape
        }

        string? code = null;
        string? status = null;
        foreach (Match m in ResultFieldRegex.Matches(response.Body))
        {
            if (string.Equals(m.Groups[1].Value, "fn", StringComparison.OrdinalIgnoreCase))
            {
                code = m.Groups[2].Value.Trim();
            }
            else
            {
                status = m.Groups[2].Value.Trim();
            }
        }

        if (status is null)
        {
            return response;   // not this shape either — let the parser report what actually arrived
        }

        string json = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string?> { ["file_code"] = code, ["file_status"] = status },
        });

        return new HttpResponseSnapshot(response.StatusCode, json, response.SetCookies, response.LocationHeader, response.ETag);
    }

    /// <summary>Drops entries from attempts that resolved a page and then never uploaded — a cancel
    /// between the two. An hour is far longer than the gap ever is, so a live attempt can't be swept
    /// out from under itself.</summary>
    private void SweepAbandonedPageFields()
    {
        DateTime cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (KeyValuePair<Guid, UploadPageFields> entry in _pageFieldsByAttempt)
        {
            if (entry.Value.StoredUtc < cutoff)
            {
                _pageFieldsByAttempt.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Twelve digits, as this fork's own uploader mints. It names the node's staging slot,
    /// so two uploads must never share one.</summary>
    internal static string NewUploadId()
    {
        Span<char> digits = stackalloc char[12];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(digits);
    }

    /// <summary>Test seam: the routes this fork moved, all three of which fail somewhere different
    /// when wrong.</summary>
    internal (string Login, string Upload, string Account) RoutesForTests => (SignInPageUrlForTests, UploadFormUrl, WebFormAccountPageUrl);

    /// <inheritdoc cref="RoutesForTests"/>
    internal HttpResponseSnapshot NormalizeForTests(HttpResponseSnapshot response) => NormalizeUploadResponse(response);

    /// <inheritdoc cref="RoutesForTests"/>
    internal bool LooksSignedInForTests(string html) => LooksSignedIn(html);

    /// <summary>Test seam for the two halves of the per-attempt handover, so the interleaving that
    /// two concurrent uploads produce can be asserted without racing them.</summary>
    internal Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> ResolveForTests(AttemptContext ctx, string html)
        => ResolveWebFormUploadServerAsync(ctx, html, "xfss-value", CancellationToken.None);

    /// <inheritdoc cref="ResolveForTests"/>
    internal Dictionary<string, string> BuildFieldsForTests(AttemptContext ctx, string sessId) => BuildClassicExtraFields(ctx, sessId);

    /// <summary>The pair of hidden fields the upload page hands over, and when they were taken.</summary>
    private sealed record UploadPageFields(string UsrId, string SrvTmpUrl, DateTime StoredUtc);
}
