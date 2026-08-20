// <copyright file="ClicknuploadPipeline.cs" company="CSUploader">
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
/// Clicknupload (clicknupload.click) — a genuinely stock XFileSharing host on the base's web-form
/// (no-API) path, built from a browser capture of a real signed-in upload 2026-07-31. It is the
/// closest thing to a drop-in the family has produced: sign in for the <c>xfss</c> cookie, GET the
/// logged-in page, scrape the form <c>action</c> + hidden <c>sess_id</c>, post a classic multipart →
/// <c>[{"file_code":"&lt;code&gt;","file_status":"OK"}]</c>, link <c>clicknupload.click/&lt;code&gt;</c>.
/// <para>
/// <b>Its multipart field set is the family default, byte for byte</b> — all nine fields
/// (<c>sess_id</c>, <c>utype=reg</c>, <c>file_descr</c>, <c>file_public=1</c>, <c>link_rcpt</c>,
/// <c>link_pass</c>, <c>to_folder</c>, <c>upload=Start upload</c>, <c>keepalive=1</c>) match what the
/// base already sends, so unlike isra.cloud and Uploady this needs no
/// <c>BuildClassicExtraFields</c> override. The cookie is the family default too (<c>xfss</c> on
/// <c>.clicknupload.click</c>, 30-day).
/// </para>
/// <para>
/// Two deviations, both about WHERE things live rather than what they look like:
/// <list type="bullet">
///   <item>The uploader is on <b><c>?op=my_account.html</c></b>; there is no <c>?op=upload_form</c>.
///   This deployment suffixes its <c>op</c> values with <c>.html</c>, which is also why the login page
///   is <c>?op=login</c> rather than the family's <c>/login.html</c>.</item>
///   <item>Storage is a line of text in a hidden header div ("Used space: <c>0.00 GB</c>") rather than
///   the stock <c>class="storage"</c> bar — see <see cref="ParseStorageUsage"/>. Only "used" is
///   published (the neighbouring "Traffic available today" is bandwidth, not a storage quota), so
///   Available renders "Unlimited".</item>
/// </list>
/// </para>
/// <para>
/// <b>Account-only, by the host's decision.</b> Anonymous upload is off: posting the standard
/// anonymous shape to the <c>ServerURL</c> its own <c>?op=api_get_limits</c> advertises answers
/// <c>[{"file_code":"undef","file_status":"uploads are not enabled for your account type"}]</c>
/// (probed 2026-07-31). So <see cref="XFileSharingApiPipeline.SupportsAnonymousUpload"/> is left at
/// the base's <c>false</c> — do not enable it without a fresh probe showing otherwise.
/// </para>
/// <para>
/// <b>The domain rotates</b> (<c>.click</c> / <c>.org</c> / <c>.co</c> / <c>.vip</c> have all been
/// used). Everything here derives from <see cref="Host"/>, so a move is a one-line change — which is
/// the reason no URL below is spelled out twice.
/// </para>
/// </summary>
public sealed class ClicknuploadPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    // "Used space: <strong>0.00 GB</strong>" inside the (display:none) UserHead div. Anchored on the
    // label because the same div also carries "Balance: $0" and "Traffic available today: Unlimited",
    // and the latter is a bandwidth figure that must never be mistaken for a storage quota.
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s+space:\s*<strong>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</strong>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The account name appears exactly once on the page, in the Connection info table:
    //   <tr><td>FTP Login:</td><td>the_account_name</td></tr>
    // There is no fa-user menu here, so the family's username scrape finds nothing — and because this
    // is a session-cookie hoster the dialog collects no username either, which would leave the account
    // showing a BLANK name in the wizard and Accounts grid.
    private static readonly Regex _ftpLoginRegex = new(
        """FTP\s+Login:\s*</td>\s*<td[^>]*>\s*([^<\s][^<]*?)\s*</td>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ClicknuploadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the form-page GET and the multipart upload from canned
    /// responses.</summary>
    internal ClicknuploadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Clicknupload";

    /// <summary>Free downloads are captcha-gated: its premium table crosses "No downloads
    /// captcha" for Free and Registered (premium.html, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    /// <summary>From its own premium.html (read 2026-08-12): guest 10, registered 35, premium 60 -
    /// all "days after last download".</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(10)
            : credentials.AccountType == AccountType.Premium ? FileRetention.DaysAfterLastDownload(60)
            : FileRetention.DaysAfterLastDownload(35);

    protected override string Host => "https://clicknupload.click";

    /// <summary>Web-form (no-API) hoster — see <see cref="XFileSharingApiPipeline.UsesWebFormUpload"/>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>This deployment's <c>op</c> routes carry no separate login page; sign-in is
    /// <c>?op=login</c>, not the family's <c>/login.html</c>.</summary>
    protected override string LoginPagePath => "/?op=login";

    /// <summary>The uploader lives on the account page here — there is no <c>?op=upload_form</c>.</summary>
    protected override string UploadFormUrl => AccountPageUrl;

    /// <summary>Same page again: it carries the logout link, the storage line and the upload form, so
    /// one fetch serves the signed-in check, the storage scrape and the upload-server discovery.</summary>
    protected override string WebFormAccountPageUrl => AccountPageUrl;

    /// <summary>
    /// 2 GB per file, the figure the host publishes itself via <c>?op=api_get_limits</c>
    /// (<c>&lt;MaxUploadFilesize&gt;2048&lt;/MaxUploadFilesize&gt;</c>, i.e. MB). Read as binary —
    /// XFileSharing's limits are 1024-based, and a clean 2048 is exactly the shape that convention
    /// produces. Guests are refused outright rather than capped, so this applies to every upload that
    /// can actually happen here.
    /// </summary>
    public override long? MaxFileSize => 2048L * 1024 * 1024;

    private string AccountPageUrl => Host + "/?op=my_account.html";

    /// <summary>
    /// Reads "Used space" out of the account page's header line. No quota is published — the
    /// neighbouring "Traffic available today" is a bandwidth allowance, not storage — so quota stays
    /// null and the grid shows Available as "Unlimited".
    /// </summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match used = _usedSpaceRegex.Match(html);
        return (used.Success ? ParseSizeToBytes(used.Groups[1].Value, used.Groups[2].Value) : null, null);
    }

    /// <summary>
    /// Reads the account name from the Connection info table's "FTP Login" row — the one place this
    /// theme prints it. Without this the account would show up unnamed, since the family scrape wants
    /// an <c>fa-user</c> menu this page doesn't have and session-cookie hosters collect no username.
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _ftpLoginRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: re-reads the account page with the
    /// stored <c>xfss</c> cookie (never a WebView). Delegates to the base helper, which returns null
    /// when there's no usable stored session.
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyFilesAsync(credentials, handler, proxy, ct);
}
