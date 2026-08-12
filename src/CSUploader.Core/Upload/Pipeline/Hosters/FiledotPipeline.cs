// <copyright file="FiledotPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;
using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// filedot.to — XFileSharing on the base's web-form (no-API) path, built from a browser capture of a
/// real signed-in upload 2026-08-02. Sign in for the <c>xfss</c> cookie, ask <c>GET /server</c> for a
/// node, post a classic multipart to <c>&lt;node&gt;/upload.cgi</c> →
/// <c>[{"file_code":"&lt;code&gt;","file_status":"OK"}]</c>, link <c>filedot.to/&lt;code&gt;</c>.
/// <para>
/// <b>Why not the REST API.</b> It exists — <c>/api/upload/server</c> and <c>/api/account/info</c>
/// both answer the family's <c>{"status":400,"msg":"Invalid key"}</c> — but nothing in the signed-in
/// UI hands a user a key: My Account, My Files, Reports and Earn Money were all walked and none
/// publishes one. An API a user cannot get a credential for is not a route this app can ship, which
/// is the same call made for DDownload.
/// </para>
/// <para>
/// <b>Account-only, by the host's decision.</b> Posting the anonymous shape (empty <c>sess_id</c>,
/// <c>utype=anon</c>) to its own node answers <c>[{"file_code":"undef","file_status":"uploads are
/// not enabled for your account type"}]</c> — word for word what Clicknupload says (probed
/// 2026-08-02, both <c>utype=anon</c> and <c>utype=reg</c>). So
/// <see cref="XFileSharingApiPipeline.SupportsAnonymousUpload"/> stays at the base's false.
/// </para>
/// <para>
/// <b>The one real deviation is where the node comes from.</b> This fork's file form carries no
/// <c>action</c> at all — its script fetches <c>GET /server</c> → <c>{"url":"https://fsNN.cobytes.cc/cgi-bin"}</c>
/// and posts there. The only <c>action</c> on the page belongs to the URL-uploader
/// (<c>…/upload.cgi?upload_type=url</c>), so the family's scrape would have found a real-looking URL
/// that quietly uploads nothing. Hence <see cref="ResolveWebFormUploadServerAsync"/>. Note the node
/// is on a DIFFERENT domain (cobytes.cc) and answers CORS preflights, which is why the browser sends
/// an OPTIONS first; we don't need to.
/// </para>
/// <para>
/// Routes are "pretty" rather than <c>?op=</c>: <c>/login.html</c> (which happens to match the family
/// default), <c>/upload/</c>, <c>/account</c>, <c>/logout/</c>.
/// </para>
/// </summary>
public sealed class FiledotPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    /// <summary>
    /// From the upload page's own uploader config (<c>ext_not_allowed: 'exe|jpg|jpeg|gif|png'</c>,
    /// read live 2026-08-02). Held as a snapshot rather than re-read per upload, for the reason
    /// Uploadrar's is: one extra request per file to catch a list that rarely moves, and a stale entry
    /// fails no worse than today — the server still refuses, just later and at the cost of the
    /// transfer.
    /// <para>
    /// Note this blocks IMAGES, not video — the opposite of Uploadrar. Nothing about this family
    /// predicts which; read each host's own list.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "jpg", "jpeg", "gif", "png",
    };

    // <TR><TD>Used space</TD><TD><b>0.00 of 10240 GB</b></TD></TR> — one unit for both figures.
    // Anchored on the label because the NEXT row is "Traffic available today <b>5120 Mb</b>", a daily
    // bandwidth allowance that must never be read as storage. (It also happens to equal the per-file
    // size limit, so a loose match would look plausible and be wrong twice over.)
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s+space\s*</TD>\s*<TD[^>]*>\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s+of\s+([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <TR><TD>Username</TD><TD><b>the_account_name</b></TD></TR> in the Account Details table. The
    // family's fa-user menu scrape finds nothing on this theme, and a session-cookie hoster collects
    // no username in the dialog — without this the account shows up blank in the wizard and grid.
    private static readonly Regex _usernameRegex = new(
        """<TD[^>]*>\s*Username\s*</TD>\s*<TD[^>]*>\s*<b>\s*([^<\s][^<]*?)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FiledotPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — the getOverride serves BOTH the upload page and the node lookup (they are
    /// distinguishable by URL); the uploadOverride stands in for the multipart post.</summary>
    internal FiledotPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Filedot";

    /// <summary>From its own premium.html (read 2026-08-12): registered "1000 days after last
    /// download", premium "Never". The guest column is a dash there, matching a host with no guest
    /// upload at all.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.Unspecified
            : credentials.AccountType == AccountType.Premium ? FileRetention.Permanent
            : FileRetention.DaysAfterLastDownload(1000);

    protected override string Host => "https://filedot.to";

    /// <summary>Web-form (no-API) hoster — see <see cref="XFileSharingApiPipeline.UsesWebFormUpload"/>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>The uploader is its own page here, not <c>?op=upload_form</c>.</summary>
    protected override string UploadFormUrl => Host + "/upload/";

    /// <summary>Storage, the account name and the logout link all live on <c>/account</c>.</summary>
    protected override string WebFormAccountPageUrl => Host + "/account";

    /// <summary>
    /// 5 GB, from the upload page's own line ("Max file size is 5120 Mb"). Read as binary: this
    /// family's limits are 1024-based and a clean 5120 is exactly what that convention produces.
    /// Guests are refused outright rather than capped, so this applies to every upload that can
    /// happen here.
    /// </summary>
    public override long? MaxFileSize => 5120L * 1024 * 1024;

    private string NodeLookupUrl => Host + "/server";

    /// <summary>
    /// This fork links a plain <c>/logout/</c>, so the family's <c>?op=logout</c> probe would call a
    /// perfectly good sign-in logged-out — the same trap DDownload's dashboard set.
    /// </summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("/logout/", StringComparison.OrdinalIgnoreCase)
           || html.Contains("op=logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a blocked extension before a byte moves. This host enforces its list at the upload
    /// itself, so without this the whole transfer is spent to earn <c>file_status</c>: "extension not
    /// allowed". The base's PreflightRejection defaults to this, so the upload path needs no separate
    /// override.
    /// <para>
    /// It is also what the UPLOAD WIZARD calls, so these files are dropped from filedot's
    /// column and names them in the warning panel <b>before the user presses Next</b> — rather than
    /// each one spending its whole transfer to earn a refusal. One rule, two consumers.
    /// </para>
    /// </summary>
    public override string? RejectedFileExtensionReason(string fileName)
        => IsBlockedExtension(fileName)
            ? $"filedot.to doesn't accept {Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant()} files "
                + $"(it blocks {string.Join(", ", BlockedExtensions.Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant()}). "
                + "Archive the file first — .rar/.zip parts upload normally."
            : null;

    /// <summary>
    /// The capture's field set, verbatim: no <c>link_rcpt</c>, no <c>upload</c> button, no
    /// <c>keepalive</c>, and <c>file_public=0</c> rather than the family's 1. This parser is
    /// field-presence sensitive (see <c>brupload-multipart-quirks</c>), so the proven set is
    /// replicated rather than the base's near-miss reused.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["file_descr"] = string.Empty,
        ["file_public"] = "0",
    };

    /// <summary>
    /// Takes the node from <c>GET /server</c> instead of the page's form <c>action</c> — the file
    /// form here has none, and the one action present belongs to the URL-uploader. The lookup is
    /// keyless (no cookie needed), but the <c>sess_id</c> still comes from the page, because that is
    /// what proves the session is alive: <c>/server</c> answers a signed-out caller just as happily,
    /// and an upload carrying a stale <c>sess_id</c> would be treated as anonymous rather than
    /// rejected.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> ResolveWebFormUploadServerAsync(
        AttemptContext ctx, string uploadFormHtml, string xfss, CancellationToken ct)
    {
        if (!LooksSignedIn(uploadFormHtml))
        {
            return (null, null, "the upload page came back logged out — the session may have expired", true);
        }

        string json;
        try
        {
            json = await GetAsync(ctx, NodeLookupUrl, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, null, "upload node lookup failed: " + ex.Message, false);
        }

        (string? node, string? error) = ParseNode(json);
        return node is null
            ? (null, null, error, false)
            : (node + "/upload.cgi", ScrapeSessId(uploadFormHtml, xfss), null, false);
    }

    /// <summary>
    /// Reads <c>{"url":"https://fsNN.cobytes.cc/cgi-bin"}</c>. Internal for testing.
    /// </summary>
    internal static (string? Node, string? Error) ParseNode(string json)
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
            // fall through to the same message — an unparseable body and a body without a url are
            // the same problem to the user.
        }

        string trimmed = json.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return (null, "filedot.to returned no upload node: " + (trimmed.Length > 160 ? trimmed[..160] + "…" : trimmed));
    }

    /// <summary>True when this host will refuse the file on its extension. Internal for testing.</summary>
    internal static bool IsBlockedExtension(string fileName)
        => BlockedExtensions.Contains(Path.GetExtension(fileName).TrimStart('.'));

    /// <summary>
    /// Reads "Used space <c>0.00 of 10240 GB</c>" off the account page. Unlike most of this family
    /// the quota IS published, so Available shows a real figure rather than "Unlimited".
    /// </summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match m = _usedSpaceRegex.Match(html);
        return m.Success
            ? (ParseSizeToBytes(m.Groups[1].Value, m.Groups[3].Value), ParseSizeToBytes(m.Groups[2].Value, m.Groups[3].Value))
            : (null, null);
    }

    /// <summary>Reads the account name from the Account Details table — see <see cref="_usernameRegex"/>.</summary>
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
