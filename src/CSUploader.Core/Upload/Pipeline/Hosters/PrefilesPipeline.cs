// <copyright file="PrefilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// PreFiles (prefiles.com) — stock XFileSharing on the classic web-form upload, <b>account-only</b>,
/// <b>512 MB</b> per file. A thin shim: the signed-in form lives on the family's own
/// <c>?op=upload_form</c>, its <c>sess_id</c> is the <c>xfss</c> cookie, and one multipart POST to the
/// node it names answers <c>[{"file_status":"OK","file_code":…}]</c> → <c>prefiles.com/&lt;code&gt;</c>.
/// No Cloudflare challenge to this client.
/// <para>
/// <b>⚠ Anonymous is refused, and its <c>?op=api_get_limits</c> makes that look otherwise.</b> That
/// call answers a signed-OUT caller in full — a node in <c>&lt;ServerURL&gt;</c> and
/// <c>&lt;MaxUploadFilesize&gt;512&lt;/MaxUploadFilesize&gt;</c> — which is exactly the seam that
/// turned out to make <see cref="UpZurPipeline"/> and <see cref="BtaFilePipeline"/> anonymous. Here it
/// does not: posting the family's guest field set to that very node answers
/// <c>[{"file_status":"uploads are not enabled for your account type","file_code":"undef"}]</c>. The
/// 512 MB is the site's global figure, not a guest permission — <b>only the upload's answer settles
/// this, and on this host it says no</b>.
/// </para>
/// <para>
/// Its routes are rewritten (<c>/login</c>, <c>/register</c>, <c>/my-account</c>, <c>/logout</c>)
/// rather than the family's <c>?op=</c> ones, so the login page and the credential POST both move —
/// but <c>?op=upload_form</c> and <c>?op=api_get_limits</c> still work, which is why only the login
/// needs overriding.
/// </para>
/// </summary>
public sealed class PrefilesPipeline : XFileSharingApiPipeline
{
    /// <summary>The upload page's own <c>max_upload_filesize: '512'</c>, and the same figure its
    /// keyless limits call reports. Binary, as XFileSharing's limits are 1024-based.</summary>
    private const long MaxFileSizeBytes = 512L * 1024 * 1024;

    public PrefilesPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal PrefilesPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    public override string Name => "PreFiles";

    /// <summary>Free downloads are captcha-gated: its pricing comparison lists "No downloads
    /// captcha" as a PRO perk (prefiles.com/pricing, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    protected override string Host => "https://prefiles.com";

    /// <summary>Measured at the node, not read off a page: the guest field set earns "uploads are not
    /// enabled for your account type".</summary>
    public override bool SupportsAnonymousUpload => false;

    /// <summary>No REST API to key off — <c>/api/upload/server</c> answers a plain 404 page — so the
    /// account uploads through the logged-in web form.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Its login is a plain <c>login</c>/<c>password</c> form with no captcha of any kind
    /// (checked live), and posting it from this app's own stack answers <c>302 + Set-Cookie: xfss</c>.
    /// So no sign-in window opens.</summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>This fork rewrote its routes: the family's <c>/login.html</c> is not where the form
    /// is.</summary>
    protected override string LoginPagePath => "/login";

    /// <summary>…and the credential POST goes to that same route rather than the site root, which is
    /// where the family default would send it.</summary>
    protected override string DirectLoginPostUrl => Host + "/login";

    /// <summary>
    /// The account page moved with the rest of the routes. The family default (<c>?op=my_files</c>)
    /// does not carry this fork's signed-in chrome, so the check read a good session as a failed one:
    /// "Signed in, but the account page didn't load as logged-in" — after a sign-in that had, in fact,
    /// just worked. <c>/my-account</c> is where the name and the logout link live.
    /// </summary>
    protected override string WebFormAccountPageUrl => Host + "/my-account";

    /// <summary>
    /// This fork links a plain <c>/logout</c>, not the family's <c>?op=logout</c> — the same quirk
    /// DDownload and DataNodes have, and the same consequence: the stock probe rejects a perfectly
    /// good session. Accepts either.
    /// </summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase)
           || html.Contains("/logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <b>Deliberately reports no name at all</b>, which is better than either name this page offers.
    /// <para>
    /// Its header reads <c>&lt;i class="fa fa-user"&gt;&lt;/i&gt;Hi, &lt;a href="…/my-account"&gt;Lynford
    /// Audie&lt;/a&gt;!</c>. The family default anchors on that icon and takes the next token, so it
    /// saved every account as <b>"Hi"</b> — the same shape of wrong answer that made Uploady's accounts
    /// all read "Profile". Reading the anchor instead yields the account's DISPLAY name, and that is
    /// worse than useless here: what this app stores as the username is what the next sign-in POSTs,
    /// and this host signs in with the EMAIL. A display name with a space in it would simply stop the
    /// account working.
    /// </para>
    /// <para>
    /// Returning null leaves the base to keep the address the user typed, which is both correct and
    /// the thing they would recognise. See also Filestank, where the same rule was learned the other
    /// way round.
    /// </para>
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        _ = html;
        return null;
    }

    /// <summary>512 MB, stated twice by the host: the upload page's <c>max_upload_filesize</c> and its
    /// keyless limits call. The same figure either side of a sign-in, so there is no separate guest
    /// number to report — and no guest upload to report it for.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
    {
        _ = credentials;
        return MaxFileSizeBytes;
    }

    /// <summary>Test seams — these three decide which paths this host takes, and none is observable
    /// from outside the family otherwise.</summary>
    internal bool UsesWebFormUploadForTests => UsesWebFormUpload;

    /// <inheritdoc cref="UsesWebFormUploadForTests"/>
    internal bool SupportsDirectLoginForTests => SupportsDirectLogin;

    /// <inheritdoc cref="UsesWebFormUploadForTests"/>
    internal (string Page, string Post) LoginRoutesForTests => (SignInPageUrlForTests, DirectLoginPostUrl);
}
