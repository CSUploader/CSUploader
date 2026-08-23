// <copyright file="UpZurPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UpZur (upzur.com) — classic XFileSharing with a LIVE anonymous upload, verified 2026-08-06 by
/// uploading real bytes: the family's anonymous field set posted to the node answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c>, and <c>upzur.com/&lt;code&gt;</c> serves a download
/// page naming the file. It was offered on a candidate list marked <i>"Sign-Up Required"</i>; it is not.
/// <para>
/// <b>Its homepage renders no upload form</b>, so the base's usual scrape (a
/// <c>&lt;form action="…/upload.cgi…"&gt;</c> on the landing page) finds nothing here. The node comes
/// from the host's own API instead — <c>?op=api_get_limits</c>, which every stock XFS exposes
/// keylessly:
/// <code>
/// &lt;Data&gt;&lt;ExtAllowed&gt;&lt;/ExtAllowed&gt;&lt;ExtNotAllowed&gt;&lt;/ExtNotAllowed&gt;
///   &lt;MaxUploadFilesize&gt;200&lt;/MaxUploadFilesize&gt;
///   &lt;ServerURL&gt;https://systeme.upzur.com/cgi-bin&lt;/ServerURL&gt;
///   &lt;SessionID&gt;&lt;/SessionID&gt;&lt;SiteName&gt;UpZur&lt;/SiteName&gt;&lt;/Data&gt;
/// </code>
/// That is the sturdier source anyway: an HTML landing page is subject to WAF and marketing
/// variation, where this contract is the one the host's own clients use. Same reasoning as
/// <see cref="SendNowPipeline"/> preferring <c>/api/upload/server</c> — but note UpZur has no such
/// route (it 404s), which is why the limits call carries the node here.
/// </para>
/// <para>
/// <b>200 MB anonymous</b>, per <c>MaxUploadFilesize</c> above — read from the keyless call, so it is
/// the guest figure. The candidate list advertised "5GB / 1.95TB"; those are the paid tiers, and the
/// host's own API is the authority over a third-party list. <c>ExtNotAllowed</c> is empty, so unlike
/// Uploadrar and filedot there is nothing to reject up front.
/// </para>
/// </summary>
public sealed class UpZurPipeline : XFileSharingApiPipeline
{
    /// <summary>The keyless limits call, which is also where the upload node comes from.</summary>
    private const string ApiGetLimitsPath = "/?op=api_get_limits";

    /// <summary>&lt;ServerURL&gt;https://systeme.upzur.com/cgi-bin&lt;/ServerURL&gt; — the cgi-bin
    /// DIRECTORY, not the script, so the script name is appended below.</summary>
    private static readonly Regex _serverUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The account name, which this fork renders in exactly ONE place on <c>?op=my_files</c> — the
    /// script that builds the "your public folder" box:
    /// <c>$(input).attr('value', 'https://upzur.com/users/&lt;name&gt;/')</c>.
    /// <para>
    /// The family default anchors on the <c>fa-user</c> account-menu icon, and this theme has no such
    /// icon anywhere, so it returned null and the account saved with a blank name. Anchoring on the
    /// <c>/users/</c> path rather than on nearby chrome is deliberate: that path segment can only be
    /// an account name, whereas an icon-adjacent token is whatever the theme put next to the icon —
    /// which is how Uploady's accounts all saved as "Profile" and EliteFile's as "Settings".
    /// </para>
    /// <para>Present on an account with no files at all (checked before and after an upload), so an
    /// empty account still gets its name.</para>
    /// <para>
    /// The storage bar that used to be overridden here moved to the base once BtaFile turned up on the
    /// identical <c>div.freespace</c> / <c>id="occupied"</c> theme — see
    /// <see cref="XFileSharingApiPipeline.TryParseStorageBar"/>.
    /// </para>
    /// </summary>
    private static readonly Regex _usernameRegex = new(
        """upzur\.com/users/([A-Za-z0-9._-]+)/""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UpZurPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal UpZurPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    public override string Name => "UpZur";

    /// <summary>Downloads are captcha-free: its premium comparison grants "No downloads
    /// captcha" to the FREE tier too, and the live free flow yields a direct link
    /// (2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    protected override string Host => "https://upzur.com";

    /// <summary>Verified by uploading a file and fetching the resulting page — not by a form being
    /// rendered, which is what DropGalaxy, Uploady and Clicknupload each had while refusing the
    /// bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// <b>This host has no API at all</b>, so an account signs in for the <c>xfss</c> cookie and
    /// uploads through the logged-in <c>?op=upload_form</c> — it must never take the API-key path.
    /// Measured, not assumed: <c>/api/upload/server</c> answers <b>404</b> and
    /// <c>/api/account/info</c> answers a <b>500 HTML error page</b>, not JSON.
    /// <para>
    /// Without this the base's username/password path opens the sign-in browser and then scrapes
    /// <c>my_account</c> for an API key that is never rendered — so the check fails <i>after</i> a
    /// perfectly good sign-in, which reads like the password was wrong.
    /// </para>
    /// <para>
    /// The sign-in browser still appears; that is how this app signs in to XFileSharing hosts (a human
    /// solves whatever the login page asks for). UpZur's own login is a plain <c>op=login</c> form with
    /// <b>no captcha</b>, so it is one of the quieter ones.
    /// </para>
    /// </summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>
    /// <b>The family default <c>/login.html</c> does not exist here — it bounces to the homepage in two
    /// hops</b> (<c>301 → /login</c>, then <c>302 → /</c>), so the sign-in window opened, landed on the
    /// front page and offered nothing to sign in with. The login form lives on the <c>op</c> route:
    /// <c>?op=login</c>, posting <c>op/login/password/token/rand/redirect</c> to the site root and
    /// answering <c>302 + Set-Cookie: xfss</c> → <c>?op=my_files</c>.
    /// <para>
    /// Confirmed against the live host with a real account (2026-08-07): the sign-in has <b>no
    /// captcha</b>, and <c>?op=my_files</c> — which the base already uses as the account page — renders
    /// the account name and its storage bar ("0 MB of 1953.1 GB"). ⚠ <c>?op=my_account</c>, the
    /// family's usual account page, <b>302s to the homepage even when signed in</b>; nothing here may
    /// depend on it.
    /// </para>
    /// </summary>
    protected override string LoginPagePath => "/?op=login";

    /// <summary>
    /// <b>No browser needed to sign in here.</b> Checked rather than assumed: the login page carries no
    /// captcha of any kind, Cloudflare fronts the site only passively (<c>cf-cache-status: DYNAMIC</c>,
    /// no challenge), and posting the form from this app's own HTTP stack answers
    /// <c>302 + Set-Cookie: xfss</c> — verified against a real account 2026-08-07.
    /// <para>
    /// So this host stores a username and password like a classic hoster, and the session is acquired
    /// on demand. The family default stays browser-based because most of these hosts gate login behind
    /// a captcha or a managed challenge, and a human has to answer those.
    /// </para>
    /// </summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>The host's own <c>MaxUploadFilesize</c> (MB) from the keyless limits call. Binary,
    /// as XFileSharing's limits are 1024-based.</summary>
    private const long AnonymousMaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>Guest cap. The account path keeps the family default — no account has been used here,
    /// so nothing stronger is claimed for it.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : base.MaxFileSizeFor(credentials);

    /// <summary>Test seams for the two account-page scrapes. The username one is a host-specific
    /// override of a family default that silently returns nothing here; the storage one now goes
    /// through the base, and pins that the <c>id="occupied"</c> bar this theme uses still reaches it.</summary>
    internal string? ParseAccountUsernameForTests(string html) => ParseAccountUsername(html);

    /// <inheritdoc cref="ParseAccountUsernameForTests"/>
    internal (long? Used, long? Quota) ParseStorageUsageForTests(string html) => ParseStorageUsage(html);

    /// <inheritdoc/>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Reads the node out of <c>?op=api_get_limits</c> rather than off a form, because this host
    /// renders no anonymous form to scrape. The query appended to the script is the family's own
    /// (<c>upload_type=file&amp;utype=anon</c>) — the same request that was verified live.
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

        Match m = _serverUrlRegex.Match(xml);
        if (!m.Success)
        {
            if (LooksLikeCloudflareChallenge(xml))
            {
                return (null,
                    $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of "
                    + "the limits call. A managed challenge validates the browser itself (TLS fingerprint, "
                    + "JS execution), so no header or cookie sent from here can satisfy it.");
            }

            return (null, $"{Name}: ?op=api_get_limits carried no <ServerURL>: {Snippet(xml)}");
        }

        string node = m.Groups[1].Value.TrimEnd('/');
        return ($"{node}/upload.cgi?upload_type=file&utype=anon", null);
    }
}
