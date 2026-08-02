// <copyright file="UploadyPipeline.cs" company="CSUploader">
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
/// Uploady (uploady.io) — classic XFileSharing running the base's web-form (no-API) path:
/// WebView sign-in for the <c>xfss</c> cookie, then GET the logged-in <c>?op=upload_form</c>, scrape
/// the form <c>action</c> (<c>sN.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&amp;utype=reg&amp;ptype=ppd</c>
/// — the upload nodes live on a separate domain) and the hidden <c>sess_id</c>, and post the file as
/// a classic single multipart → <c>[{"file_code":"&lt;code&gt;","file_status":"OK"}]</c>, link
/// <c>https://uploady.io/&lt;code&gt;</c>. Verified end-to-end against a browser capture 2026-07-27.
/// <para>
/// <b>Anonymous upload is NOT offered here, and must not be re-enabled without new evidence</b> — see
/// <see cref="SupportsAnonymousUpload"/>. It is offered by the site and it is broken on the site.
/// </para>
/// <para>
/// Two further deviations from the family, both consequences of Uploady running a heavily re-skinned
/// dashboard rather than the stock XFS template:
/// <list type="bullet">
///   <item>Its <c>?op=my_files</c> file manager carries no storage figures at all, so the account
///   check and storage refresh read <c>?op=my_account</c> instead — see
///   <see cref="WebFormAccountPageUrl"/> and <see cref="ParseStorageUsage"/>.</item>
///   <item>Neither page exposes the username in a shape the family scrape recognises, so the account
///   grid keeps the name the user typed. Cosmetic; sign-in itself keys on the session cookie.</item>
/// </list>
/// Its REST API is left alone deliberately: <c>?op=my_account</c> reports "No API Key Found" with a
/// generate-it link, so the API path would have to mint a key before it could be tried, and no
/// captured evidence says the resulting key uploads. The web form is the flow that is actually proven,
/// so that is the flow that ships.
/// </para>
/// </summary>
public sealed class UploadyPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    // Storage figures on Uploady's ?op=my_account dashboard. Both anchor on their own label because
    // the page renders SEVEN "dash-stat-value" cells and two of them hold a size — the other being
    // Bandwidth Usage ("10.00 GB remaining"), which an unanchored scrape would happily mistake for
    // the quota. Verified against the captured page 2026-07-27: one match each.
    //   <div class="dash-stat-label">Storage Usage</div>
    //   <div class="dash-stat-value">1000.00 <small>GB total</small></div>
    private static readonly Regex _storageQuotaRegex = new(
        """Storage\s+Usage\s*</div>\s*<div[^>]*\bclass=["']dash-stat-value["'][^>]*>\s*([0-9]+(?:[.,][0-9]+)?)\s*<small>\s*([KMGT]?B)\s+total""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    //   <span><i class="fal fa-database mr-1"></i>Space used</span> <span>0.00 MB</span>
    private static readonly Regex _storageUsedRegex = new(
        """Space\s+used\s*</span>\s*<span[^>]*>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The account name, from its explicitly labelled row:
    //   <div class="tab-row-label"><h6>Username</h6></div>
    //   <div class="tab-row-value"><span class="font-weight-bold text-dark">the_name</span></div>
    private static readonly Regex _usernameRowRegex = new(
        """<h6>\s*Username\s*</h6>\s*</div>\s*<div[^>]*>\s*<span[^>]*>\s*([^<\s][^<]*?)\s*</span>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Language-independent fallback: the account's own profile URL, which the page renders both as
    // visible text and in a copy-to-clipboard attribute. Survives the UI being switched to French etc.,
    // where the "Username" label above would not.
    private static readonly Regex _profileUrlRegex = new(
        """uploady\.io/users/([A-Za-z0-9._\-]{1,64})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UploadyPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow
    /// from canned responses.</summary>
    internal UploadyPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Uploady";

    protected override string Host => "https://uploady.io";

    /// <summary>
    /// <b>Anonymous upload is broken on Uploady's side</b> (established 2026-07-27), so the hoster is
    /// account-only. The site still renders the guest form on <c>?op=upload_form</c> and still accepts
    /// the POST — then answers <c>[{"file_code":"undef","file_status":"failed while requesting fs.cgi:
    /// &lt;500 Internal Server Error&gt;"}]</c> and bounces the browser to
    /// <c>?op=upload_result&amp;st=failed…</c>. It is not us: a Fiddler capture of <i>Firefox</i> doing
    /// the guest upload failed exactly that way, and the same capture's REGISTERED upload — same file,
    /// same upload node (<c>s5.gamezizo.com</c>), minutes apart — returned <c>file_status: OK</c>. So
    /// the node is healthy and the guest path specifically is not; retrying it only wastes the bytes.
    /// <para>
    /// This is deliberately an explicit <c>false</c> rather than silence: re-enabling it is a one-word
    /// edit, and whoever considers it should have to read why it is off first. Re-enable only after a
    /// fresh capture shows a guest upload actually succeeding — restoring
    /// <c>BuildAnonUploadFormUrl</c> (Uploady's homepage carries no form; the guest form is on
    /// <c>?op=upload_form</c>) along with it.
    /// </para>
    /// </summary>
    public override bool SupportsAnonymousUpload => false;

    /// <summary>Web-form (no-API) hoster — see <see cref="XFileSharingApiPipeline.UsesWebFormUpload"/>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>
    /// 10 GB, the registered-account per-file cap the logged-in upload page states itself
    /// (<c>max_upload_filesize: '10240'</c> MB, alongside "Maximum size: 10240 MB" and a 30-file batch
    /// limit). The guest form advertises half that (5120 MB), but guests can't upload here at all —
    /// see <see cref="SupportsAnonymousUpload"/> — so the registered figure is the only one that
    /// applies.
    /// </summary>
    public override long? MaxFileSize => 10240L * 1024 * 1024;

    /// <summary>
    /// The exact field set Uploady's own uploader posts on a SUCCESSFUL registered upload (capture
    /// 2026-07-27) — four fields, where the family default sends nine. XFileSharing's multipart parser
    /// is field-presence sensitive, so the proven set is replicated verbatim rather than risking a
    /// wasted upload on fields this deployment doesn't expect.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["file_public"] = "1",
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Uploady's storage figures live on <c>?op=my_account</c>, not on the family's usual
    /// <c>?op=my_files</c> — its file manager renders no storage bar whatsoever. The dashboard carries
    /// the logout link too, so it still doubles as the signed-in probe.
    /// </summary>
    protected override string WebFormAccountPageUrl => MyAccountUrl;

    /// <summary>
    /// Reads Uploady's re-skinned dashboard cards instead of the stock <c>class="storage"</c> bar,
    /// which this deployment doesn't render. Either figure may be absent; the base treats "both null"
    /// as nothing to report.
    /// </summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match used = _storageUsedRegex.Match(html);
        Match quota = _storageQuotaRegex.Match(html);
        return (used.Success ? ParseSizeToBytes(used.Groups[1].Value, used.Groups[2].Value) : null,
                quota.Success ? ParseSizeToBytes(quota.Groups[1].Value, quota.Groups[2].Value) : null);
    }

    /// <summary>
    /// Reads the account name from the dashboard's labelled "Username" row, falling back to the
    /// profile URL it publishes (<c>uploady.io/users/&lt;name&gt;</c>) when the UI is in another
    /// language.
    /// <para>
    /// This override exists because the family default doesn't merely miss here — it matches the WRONG
    /// THING. It anchors on the <c>fa-user</c> icon and takes the next token, and Uploady uses that
    /// icon for its <b>"Profile" tab label</b>, so every account was saved as literally "Profile":
    /// wrong, and identical for every account on the host. A silently wrong name is worse than a blank
    /// one, which is why this is pinned by a test using the real markup.
    /// </para>
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        if (_usernameRowRegex.Match(html) is { Success: true } row)
        {
            return row.Groups[1].Value;
        }

        return _profileUrlRegex.Match(html) is { Success: true } url ? url.Groups[1].Value : null;
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: re-reads the dashboard with the
    /// stored <c>xfss</c> cookie (never a WebView). Delegates to the base helper, which returns null
    /// when there's no usable stored session.
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyFilesAsync(credentials, handler, proxy, ct);
}
