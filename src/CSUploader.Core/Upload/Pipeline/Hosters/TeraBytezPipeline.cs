// <copyright file="TeraBytezPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// TeraBytez (terabytez.org) — XFileSharing on the base's web-form (no-API) path, from a browser
/// capture of a real signed-in upload 2026-08-02. Sign in for the <c>xfss</c> cookie, GET
/// <c>/upload/</c>, scrape the form <c>action</c> + hidden <c>sess_id</c>, post a classic multipart →
/// <c>[{"file_status":"OK","file_code":"…"}]</c>, link <c>terabytez.org/&lt;code&gt;</c>.
/// <para>
/// <b>Sign-in is the only route, and here that is not a judgement call:</b> this host has no REST API
/// at all. <c>/api/upload/server</c> answers a plain Apache <b>404</b> and <c>?op=api_get_limits</c>
/// serves the homepage (probed 2026-08-02). Contrast <see cref="FiledotPipeline"/>, whose API exists
/// but hands out no key, and Uploadrar, whose API is the shipping route.
/// </para>
/// <para>
/// <b>Account-only.</b> An anonymous classic post to its own node answers HTTP 500 <c>ERROR: Uploads
/// not enabled for this type of users</c>. Its <c>put_chunk.cgi</c> WILL take anonymous bytes and
/// reply <c>{"status":"OK"}</c> — and then <c>import_file</c> refuses with <c>uploads are not enabled
/// for your account type</c>. A node accepting bytes says nothing about the host accepting the file.
/// </para>
/// <para>
/// Everything else is stock, including the thing filedot.to broke: the file form here DOES carry its
/// own <c>action</c> (<c>…/upload.cgi?utype=reg</c>) and is first in document order, so the family's
/// scrape resolves the node without help. Only the routes moved — <c>/login/</c>, <c>/upload/</c>,
/// <c>/account/</c>, <c>/logout/</c> — plus this theme's own storage and username markup.
/// </para>
/// <para>
/// <b>⚠ Files EXPIRE.</b> Its plan table (read 2026-08-02) gives retention as "days after last
/// download": <b>30 for registered</b>, 365 for premium, 5 for anonymous. So an unshared link rots in
/// a month. Nothing in the client can prevent that — as with DropMeFiles, it is a property of the host
/// worth knowing before choosing it for anything archival.
/// </para>
/// <para>
/// The site sits behind <b>DDoS-Guard</b> (<c>__ddg*</c> cookies), not Cloudflare. It is passive to
/// this client today — plain requests to the homepage, <c>/upload/</c>, <c>/premium/</c> and the node
/// all answered 200 without any <c>__ddg</c> cookie. If that ever changes it fails like TakeFile did,
/// and the WebView (which does hold those cookies) is the only lead worth following.
/// </para>
/// </summary>
public sealed class TeraBytezPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    private const long RegisteredMaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>Registered-tier storage, from the plan table — the account page publishes no quota.</summary>
    private const long RegisteredStorageQuotaBytes = 10L * 1024 * 1024 * 1024;

    // <span>Used Space</span> <div class="price"><sup>GB</sup>0.00</div>
    // Note the unit precedes the number. Anchored on the label because the identical widget markup is
    // reused two boxes along for "Traffic available <sup>MB</sup>5000" — a DAILY BANDWIDTH allowance,
    // not storage, and the same misread Clicknupload's theme invites.
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s+Space\s*</span>\s*<div[^>]*class=["'][^"']*price[^"']*["'][^>]*>\s*<sup>\s*([KMGT]?B)\s*</sup>\s*([0-9]+(?:[.,][0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <label>My username</label> <input type="text" readonly class="form-control-plaintext" value="…">
    // The family's fa-user scrape must NOT be used on this theme: its user menu reads "Profile", which
    // is exactly the wrong string Uploady once showed as the account name.
    private static readonly Regex _usernameRegex = new(
        """<label[^>]*>\s*My\s+username\s*</label>\s*<input\b[^>]*?\bvalue=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public TeraBytezPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the form-page GET and the multipart upload from canned responses.</summary>
    internal TeraBytezPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "TeraBytez";

    /// <summary>Free downloads are captcha-gated: its premium table checks "No downloads
    /// captcha" for Premium only (premium.html, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    protected override string Host => "https://terabytez.org";

    /// <summary>Web-form (no-API) hoster — see <see cref="XFileSharingApiPipeline.UsesWebFormUpload"/>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Pretty route; the family's <c>/login.html</c> is not served here.</summary>
    protected override string LoginPagePath => "/login/";

    /// <summary>The uploader is its own page, not <c>?op=upload_form</c>.</summary>
    protected override string UploadFormUrl => Host + "/upload/";

    /// <summary>Carries the logout link, the storage widget and the account name.</summary>
    protected override string WebFormAccountPageUrl => Host + "/account/";

    /// <summary>
    /// 100 MB for a registered account — from the uploader's own config
    /// (<c>max_upload_filesize: '100'</c>, MB as everywhere in this family) and confirmed by the
    /// published plan table. The conservative default; see <see cref="MaxFileSizeFor"/> for premium.
    /// <para>
    /// This is the smallest per-file cap of any hoster in the tree, so it is the one most likely to
    /// bite: without it the wizard would queue release-sized files this host will not take.
    /// </para>
    /// </summary>
    public override long? MaxFileSize => RegisteredMaxFileSizeBytes;

    /// <summary>
    /// Per-file cap by tier, from the site's own plan table (read 2026-08-02): <b>premium 5000 MB</b>,
    /// <b>registered 100 MB</b>, and anonymous 10 MB — which this app never uses, the host having no
    /// anonymous upload at all. Premium classification comes from the persisted
    /// <see cref="FileHosterLoginDto.AccountType"/>; until <see cref="CheckAccountAsync"/> can detect
    /// premium off the account page (its premium indicator is uncaptured — the only capture is a free
    /// account), accounts persist as Free and stay on the conservative cap.
    /// </summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials) => credentials.AccountType switch
    {
        AccountType.Premium => 5000L * 1024 * 1024,
        _ => RegisteredMaxFileSizeBytes,
    };

    /// <summary>
    /// Retention by tier, from the same plan table as <see cref="MaxFileSizeFor"/> (read 2026-08-02),
    /// given as days after LAST download: <b>registered 30</b>, <b>premium 365</b> (and anonymous 5,
    /// which this app never uses — the host has no anonymous upload). So an unshared link rots in a
    /// month; only traffic keeps it alive.
    /// </summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials) => credentials.AccountType switch
    {
        AccountType.Premium => FileRetention.DaysAfterLastDownload(365),
        _ => FileRetention.DaysAfterLastDownload(30),
    };

    /// <summary>This fork links a plain <c>/logout/</c>, so the family's <c>?op=logout</c> probe would
    /// call a perfectly good sign-in logged-out.</summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("/logout/", StringComparison.OrdinalIgnoreCase)
           || html.Contains("op=logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capture's field set, verbatim: the family default minus the <c>upload</c> button and
    /// <c>keepalive</c>. This parser is field-presence sensitive (see <c>brupload-multipart-quirks</c>),
    /// so the proven set is replicated rather than the base's near-miss reused.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["file_public"] = "1",
        ["file_descr"] = string.Empty,
    };

    /// <summary>
    /// Reads the "Used Space" widget, and pairs it with the tier quota the account page does NOT
    /// render.
    /// <para>
    /// The homepage advertises "Unlimited Storage" and the widget shows only a used figure, so the
    /// obvious reading — no quota, Available "Unlimited" — is what this first did. The plan table says
    /// otherwise: <b>registered accounts get 10 GB</b>, and only premium is unlimited. Reporting
    /// unlimited would overstate a 10 GB account by an unbounded margin, and this app uses the figure
    /// to choose where a package fits.
    /// </para>
    /// <para>
    /// So the constant is the REGISTERED quota, which is every account this app can currently observe
    /// (premium is undetectable here — see <see cref="MaxFileSizeFor"/>). A premium user is
    /// consequently understated, which is the same safe direction the per-file cap errs in.
    /// </para>
    /// <para>
    /// The neighbouring "Traffic available" widget is byte-identical in structure and is a daily
    /// BANDWIDTH allowance — the regex is anchored on the label so it can never drift onto it.
    /// </para>
    /// </summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match m = _usedSpaceRegex.Match(html);
        return m.Success
            ? (ParseSizeToBytes(m.Groups[2].Value, m.Groups[1].Value), RegisteredStorageQuotaBytes)
            : (null, null);
    }

    /// <summary>Reads the account name from the "My username" field — see <see cref="_usernameRegex"/>.</summary>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: re-reads the account page with the
    /// stored <c>xfss</c> cookie (never a WebView). Returns null when there's no usable session.
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyFilesAsync(credentials, handler, proxy, ct);
}
