// <copyright file="WorldFilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// World Files (world-files.com) — classic XFileSharing, and <b>anonymous upload is live</b>:
/// <b>5 GB</b> as a guest, <b>10 GB</b> signed in, with a <b>500 GB</b> free account quota.
/// <para>
/// ⚠ <b>Nothing on the site offers the guest upload.</b> Signed out, <c>?op=upload_form</c> answers a
/// 302 to the login — so by the usual reading this is an account-only host. It is not: the family's
/// anonymous field set, posted to the node its own <c>?op=api_get_limits</c> names, answers
/// <c>[{"file_status":"OK","file_code":…}]</c> and the link serves a real download page. Third host on
/// that theme after <see cref="UpZurPipeline"/> and <see cref="BtaFilePipeline"/>, and the third time
/// the keyless limits call — not the HTML — was where the truth was.
/// </para>
/// <para>
/// Everything else is the family default and stays that way: <c>/login.html</c> exists (no captcha,
/// and posting the form from this app's own stack answers <c>302 + Set-Cookie: xfss</c>), the
/// signed-in <c>?op=upload_form</c> carries a real <c>&lt;form action="…/upload.cgi"&gt;</c>, the
/// reply is the family's JSON, and the link is <c>world-files.com/&lt;code&gt;</c>. The only overrides
/// left are the anonymous node lookup and the caps — this theme's name and storage markup moved to
/// the base once Xubster turned up on identical markup.
/// </para>
/// </summary>
public sealed class WorldFilesPipeline : XFileSharingApiPipeline
{
    /// <summary>The keyless limits call, which is also where the anonymous node comes from.</summary>
    private const string ApiGetLimitsPath = "/?op=api_get_limits";

    /// <summary>Guest cap — <c>&lt;MaxUploadFilesize&gt;5000&lt;/MaxUploadFilesize&gt;</c> from the
    /// signed-out limits call. Binary, as XFileSharing's limits are 1024-based.</summary>
    private const long AnonymousMaxFileSizeBytes = 5000L * 1024 * 1024;

    /// <summary>Account cap — the signed-in upload page's own <c>max_upload_filesize: '10000'</c>,
    /// twice the guest figure.</summary>
    private const long AccountMaxFileSizeBytes = 10000L * 1024 * 1024;

    /// <summary>&lt;ServerURL&gt;https://wfs04.world-files.com/cgi-bin&lt;/ServerURL&gt; — the cgi-bin
    /// DIRECTORY, not the script, so the script name is appended. ⚠ The node rotates (wfs02 in the
    /// capture, wfs04 an hour later), which is exactly why it is asked for per upload.</summary>
    private static readonly Regex ServerUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public WorldFilesPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal WorldFilesPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    /// <summary>The host's own <c>&lt;SiteName&gt;</c>, spelled as it spells it.</summary>
    public override string Name => "World Files";

    /// <summary>Free downloads are captcha-gated: its premium table checks "No downloads
    /// captcha" for Premium only (premium.html, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    /// <summary>From its own premium.html (read 2026-08-12): registered "45 days after last
    /// download". Premium says only "On the individual condition" and the guest column is empty, so
    /// both report unknown.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous || credentials.AccountType == AccountType.Premium
            ? FileRetention.Unspecified
            : FileRetention.DaysAfterLastDownload(45);

    protected override string Host => "https://world-files.com";

    /// <summary>Verified by uploading real bytes as a guest and fetching the page that came back —
    /// not by a form being rendered, because there is no guest form to render.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>No REST API to key off: <c>/api/upload/server</c> answers this fork's HTML "File Not
    /// Found" page, so the account uploads through the logged-in web form.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Its login page carries no captcha (registration does — a numeric <c>code</c> field —
    /// but that is not a route this app takes), and posting the family's form from this app's own
    /// stack answers <c>302 + Set-Cookie: xfss</c>. So no sign-in window opens.</summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// The family's <c>?op=my_account</c>, which on this fork is the page carrying both the name and
    /// the storage row — and, unlike UpZur, it loads perfectly well signed in. The base's default
    /// (<c>?op=my_files</c>) also works and shows the signed-in chrome, but has neither figure.
    /// </summary>
    protected override string WebFormAccountPageUrl => MyAccountUrl;

    /// <summary>5 GB as a guest, 10 GB signed in — both figures stated by the host, one per side of
    /// the sign-in, and the guest one measured at the node as well.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : AccountMaxFileSizeBytes;

    /// <summary>
    /// Reads the node out of <c>?op=api_get_limits</c> rather than off a form, because signed out this
    /// host renders no form at all — <c>?op=upload_form</c> 302s to the login. The query appended is
    /// the family's own, and is the exact request that was verified live.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        string xml;
        try
        {
            xml = await GetAsync(ctx, Host + ApiGetLimitsPath, NoCacheHeaders, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: upload-server lookup failed: {ex.Message}");
        }

        Match m = ServerUrlRegex.Match(xml);
        if (!m.Success)
        {
            return (null, $"{Name}: ?op=api_get_limits carried no <ServerURL>: {Snippet(xml)}");
        }

        string node = m.Groups[1].Value.TrimEnd('/');
        return ($"{node}/upload.cgi?upload_type=file&utype=anon", null);
    }

    /// <summary>
    /// Test seams for the two account-page scrapes. ⚠ Both now go through the BASE: this theme has no
    /// <c>fa-user</c> icon and states its storage as <c>Used space: 0.00 of 500 GB</c> (no unit on the
    /// used figure), and both patterns moved down once Xubster turned up on identical markup. These
    /// stay as tests so that hoisting can't silently stop reaching this host.
    /// </summary>
    internal string? ParseAccountUsernameForTests(string html) => ParseAccountUsername(html);

    /// <inheritdoc cref="ParseAccountUsernameForTests"/>
    internal (long? Used, long? Quota) ParseStorageUsageForTests(string html) => ParseStorageUsage(html);

    /// <inheritdoc cref="ParseAccountUsernameForTests"/>
    internal string AccountPageUrlForTests => WebFormAccountPageUrl;
}
