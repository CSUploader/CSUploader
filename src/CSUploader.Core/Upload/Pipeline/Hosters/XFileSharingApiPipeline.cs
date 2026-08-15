// <copyright file="XFileSharingApiPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Abstract base for XFileSharing-family hosters that expose a per-account REST API.
/// The protocol is the same across the family (verified against ex-load.com 2026-05-26):
/// <list type="bullet">
///   <item><c>GET /api/account/info?key=KEY</c> → JSON <c>{status, msg, result:{email, premium_expire, ...}}</c></item>
///   <item><c>GET /api/upload/server?key=KEY</c> → JSON <c>{status, msg, sess_id, result: "http://fsNN.HOST/cgi-bin/upload.cgi"}</c></item>
///   <item>Multipart POST to <c>result</c> with <c>sess_id</c> + the BRupload-style field
///   set (utype/file_descr/file_public/etc.), byte-shape per
///   <c>brupload-multipart-quirks</c>.</item>
///   <item>Response is <c>[{file_code, file_status}]</c>.</item>
/// </list>
/// Concrete subclasses supply just the hoster name and host URL; everything else (login
/// URL, my_account URL, cookie defaults, regexes, U/P bootstrap) is shared verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Two credential paths land at the same end state — an <see cref="FileHosterLoginDto.ApiKey"/>
/// that drives all subsequent operations:
/// </para>
/// <list type="bullet">
///   <item><b>API-key direct</b>: user pastes their key; verification is a single
///   <c>/api/account/info?key=...</c> round-trip.</item>
///   <item><b>Username/password bootstrap</b>: user types credentials, the pipeline
///   pops <see cref="IInteractiveAuthService"/> for the captcha login, GETs
///   <c>/?op=my_account</c>, scrapes the <c>api-url</c> input for the existing key
///   (generating one via <c>?op=my_account&amp;generate_api_key=1&amp;token=...</c> when
///   missing), then persists onto the DTO and discards the cookie/pin.</item>
/// </list>
/// <para>
/// Because the API key is the credential (not an IP-bound session cookie), uploads can
/// rotate proxies freely. The <see cref="FileHosterLoginDto.PinnedProxyId"/> is only used
/// during the brief bootstrap window and cleared once the API key is in hand.
/// </para>
/// <para>
/// ONE class across FIVE files — a file split by concern, not a decomposition (the paths share
/// config knobs, regexes, gates and parse helpers): this file keeps the configuration surface +
/// the API-key <see cref="RunAsync"/>; <c>.Anonymous.cs</c> the no-login web-form path;
/// <c>.WebForm.cs</c> the signed-in web-form path + xfss session management;
/// <c>.AccountCheck.cs</c> verification and the storage refresh; <c>.Transport.cs</c> the
/// classic/chunked upload protocols, HTTP/cookie plumbing and scrape utilities.
/// </para>
/// </remarks>
public abstract partial class XFileSharingApiPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    /// <summary>Hoster origin, e.g. <c>"https://ex-load.com"</c>. Must not end with a slash.</summary>
    protected abstract string Host { get; }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <summary>Override for hosters that use a different cookie name. The vast majority
    /// of XFileSharing deployments use <c>xfss</c>.</summary>
    protected virtual string CookieName => "xfss";

    /// <summary>Override for hosters whose cookie domain differs from <c>"." + Uri(Host).Host</c>.</summary>
    protected virtual string CookieDomain => "." + new Uri(Host).Host;

    /// <summary>Override for hosters whose login page lives at a non-standard path.</summary>
    protected virtual string LoginPagePath => "/login.html";

    /// <summary>
    /// Declares which XFileSharing upload protocol the hoster speaks. Defaults to the
    /// classic single-multipart POST to <c>upload.cgi</c>; override to <c>true</c> on
    /// subclasses that use the modern chunked protocol (per-chunk POST to <c>up.cgi</c>
    /// + finalize via <c>api.cgi</c>, like hxfile.co's CDN frontend). NO auto-probe /
    /// fallback — if the declaration is wrong the upload fails fast (no wasted bytes
    /// on the wrong endpoint), and the fix is a one-line override here once a Fiddler
    /// trace of the live web UI confirms which protocol the hoster actually expects.
    /// </summary>
    /// <summary>
    /// How many times the upload-server lookup may ask before giving up, counting the first. Only
    /// unreadable answers are re-asked — see <see cref="GetUploadServerAsync"/>.
    /// </summary>
    private const int UploadServerAttempts = 3;

    protected virtual bool UsesChunkedUpload => false;

    /// <summary>
    /// Whether the hoster accepts anonymous (not-logged-in) uploads via its web upload form.
    /// Defaults to false; subclasses override to true once verified (currently Hexload, whose
    /// homepage renders an <c>id="uploadfile"</c> form posting to a per-session
    /// <c>…/cgi-bin/upload.cgi?…&amp;utype=anon</c> server with an empty <c>sess_id</c>). When
    /// true and the attempt's credentials are anonymous, <see cref="RunAsync"/> routes to the
    /// no-auth web-form path (<see cref="RunAnonymousAsync"/>) instead of the API-key flow.
    /// Surfaced on the interface so the upload wizard can offer an Anonymous option alongside
    /// any saved accounts.
    /// </summary>
    public virtual bool SupportsAnonymousUpload => false;

    /// <summary>
    /// Whether the hoster authenticates uploads through the classic XFileSharing WEB FORM
    /// (the logged-in <c>?op=upload_form</c> page) rather than the per-account REST API. Defaults
    /// to false — the family's mainstream path is the API (<c>/api/account/info</c> +
    /// <c>/api/upload/server</c>). Set true on hosters that DON'T expose that API to their accounts
    /// — their <c>my_account</c> page renders no <c>api-url</c>/key (verified for isra.cloud
    /// 2026-06-26). For those:
    /// <list type="bullet">
    ///   <item><see cref="RunAsync"/> routes to <see cref="RunWebFormAsync"/>: GET
    ///   <c>?op=upload_form</c> with the <c>xfss</c> cookie, scrape the form's <c>action</c>
    ///   (the <c>fsNN/cgi-bin/upload.cgi</c> server) + the hidden <c>sess_id</c>, then reuse the
    ///   classic multipart POST + <see cref="ParseUploadResponse"/>.</item>
    ///   <item><see cref="CheckAccountAsync"/> routes to <see cref="CheckAccountViaWebFormAsync"/>:
    ///   WebView sign-in → <c>my_account</c> HTML scrape for storage/premium. The only persisted
    ///   credential is the <c>xfss</c> session cookie (no API key).</item>
    /// </list>
    /// The cookie acquisition (<see cref="GetOrAcquireXfssCookieAsync"/>), classic upload, and
    /// progress plumbing are all shared with the API path.
    /// </summary>
    protected virtual bool UsesWebFormUpload => false;

    /// <summary>Maximum file size — defaults to the standard 1 GiB free-tier cap. Override
    /// for hosters with different free-tier limits.</summary>
    public virtual long? MaxFileSize => 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// No per-package count cap. The XFileSharing protocol's documented "30 files per
    /// session" applies to a single <c>sess_id</c> obtained from
    /// <c>/api/upload/server</c>, but our upload flow fetches a fresh <c>sess_id</c>
    /// for every file (each <see cref="RunAsync"/> call handles one file and starts
    /// with its own <c>GetUploadServerAsync</c> call). So an N-file package against an
    /// XFS hoster issues N independent sessions and the documented cap never applies.
    /// Returning <c>null</c> lifts the wizard's per-hoster truncation accordingly.
    /// </summary>
    public virtual int? MaxFilesPerPackage => null;

    /// <inheritdoc/>
    public virtual long? MaxFileSizeFor(FileHosterLoginDto credentials) => MaxFileSize;

    /// <summary>
    /// <inheritdoc cref="IFileHosterPipeline.MaxConcurrentUploadsFor" path="/summary"/>
    /// <para>
    /// Declared here (rather than left to the interface's default) so subclasses can actually override
    /// it: this class is where <see cref="IFileHosterPipeline"/> enters the hierarchy, so the interface
    /// slot is bound HERE. A same-named method added further down does not claim that slot — it simply
    /// never gets called through the interface, which is how a Send.now concurrency cap silently did
    /// nothing until a test asserted it through <see cref="IFileHosterPipeline"/>.
    /// </para>
    /// </summary>
    public virtual int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials) => null;

    /// <summary>
    /// <inheritdoc cref="IFileHosterPipeline.RetentionFor" path="/summary/text()[1]"/>
    /// <para>
    /// Declared <c>virtual</c> here for the same reason as
    /// <see cref="MaxConcurrentUploadsFor"/>: the interface slot binds at this class, so a subclass
    /// method that merely shares the name would never be reached through
    /// <see cref="IFileHosterPipeline"/>.
    /// </para>
    /// <para>
    /// Nothing family-wide to declare — XFS forks set their own policy, and most publish none.
    /// </para>
    /// </summary>
    public virtual FileRetention RetentionFor(FileHosterLoginDto credentials) => FileRetention.Unspecified;

    /// <inheritdoc/>
    public bool RequiresHashingBeforeUpload => false;

    /// <inheritdoc/>
    public bool RequiresHashingAfterUpload => false;

    // ---- Derived URLs ----

    protected string LoginUrl => Host + LoginPagePath;

    /// <summary>
    /// Opt-in: this hoster's login is a plain form this app can post itself, so signing in needs no
    /// browser. Default false — the family exists because most of these hosts gate login behind a
    /// captcha or a Cloudflare challenge, and a human has to answer those.
    /// <para>
    /// Turn it on only against evidence that a headless login actually works: the login page carrying
    /// no captcha markers is necessary but not sufficient, since a challenge can be applied at the
    /// edge to this client and not to a browser. Post the form and look for the session cookie.
    /// </para>
    /// </summary>
    protected virtual bool SupportsDirectLogin => false;

    /// <summary>Where the login form POSTs. The family posts to the site root with <c>op=login</c>;
    /// override for a fork that posts elsewhere. Only consulted when <see cref="SupportsDirectLogin"/>.</summary>
    protected virtual string DirectLoginPostUrl => Host + "/";

    // <input type="hidden" name="token" value="1d0e3d56f8bb944bc7504b698f154d54"> on the login form —
    // XFileSharing's anti-CSRF field. Absent on some forks, which is fine: an empty token posts too.
    private static readonly Regex _loginTokenRegex = new(
        """name=["']token["'][^>]*?value=["']([^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Signs in by posting the login form, returning the <c>xfss</c> cookie. Two requests: GET the
    /// login page for its anti-CSRF <c>token</c>, then POST <c>op=login</c>. Success sets
    /// <c>Set-Cookie: xfss</c> on a 302 (the handler doesn't follow redirects, so it is captured);
    /// a wrong password re-renders the page as 200 with no cookie, which is the only failure signal
    /// the family gives.
    /// </summary>
    protected async Task<(string? Xfss, string? Error)> DirectLoginAsync(HttpHandler handler, string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, $"{Name} needs a username and password.");
        }

        string token = string.Empty;
        try
        {
            string page = _getOverride is not null
                ? await _getOverride(LoginUrl, null).ConfigureAwait(false)
                : (await handler.GetSnapshotAsync(LoginUrl, null, ct).ConfigureAwait(false)).Body;

            Match m = _loginTokenRegex.Match(page);
            if (m.Success)
            {
                token = m.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            return (null, $"{Name} login page fetch failed: {ex.Message}");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "login",
            ["login"] = username,
            ["password"] = password,
            ["token"] = token,
            ["rand"] = string.Empty,
            ["redirect"] = string.Empty,
        };

        HttpResponseSnapshot snapshot;
        try
        {
            snapshot = _postFormOverride is not null
                ? await _postFormOverride(DirectLoginPostUrl, form).ConfigureAwait(false)
                : await handler.PostFormAsync(DirectLoginPostUrl, form, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"{Name} login request failed: {ex.Message}");
        }

        string? xfss = ExtractCookieValue(snapshot.SetCookies, CookieName);
        return xfss is not null
            ? (xfss, null)
            : (null, $"{Name} sign-in failed — check the username and password.");
    }

    /// <summary>Pulls a named cookie's value out of a response's <c>Set-Cookie</c> list.</summary>
    private static string? ExtractCookieValue(IReadOnlyList<string> setCookies, string name)
    {
        string prefix = name + "=";
        foreach (string raw in setCookies)
        {
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string after = raw[prefix.Length..];
                int semi = after.IndexOf(';', StringComparison.Ordinal);
                string value = semi < 0 ? after : after[..semi];
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>Test seam: the page the sign-in window opens. Worth pinning per host — a fork whose
    /// login lives somewhere other than the family default silently opens a window on whatever that
    /// URL redirects to (UpZur bounced 301 → /login → 302 → the homepage), which reads to the user as
    /// a sign-in that does nothing.</summary>
    internal string SignInPageUrlForTests => LoginUrl;
    protected string MyAccountUrl => Host + "/?op=my_account";
    protected string PublicUrlPrefix => Host + "/";
    protected string ApiAccountInfoUrl => Host + "/api/account/info";
    protected string ApiUploadServerUrl => Host + "/api/upload/server";

    /// <summary>The logged-in web upload form (web-form mode only). Carries the per-session
    /// upload-server <c>action</c> and the hidden <c>sess_id</c> we scrape in
    /// <see cref="GetWebFormUploadServerAsync"/>.</summary>
    /// <summary>Virtual because forks move it: Clicknupload renders the uploader on
    /// <c>?op=my_account.html</c> and has no <c>?op=upload_form</c> at all.</summary>
    protected virtual string UploadFormUrl => Host + "/?op=upload_form";

    /// <summary>The logged-in file manager (web-form mode only). Source of the account's storage bar
    /// (<c>used of total</c>), the username, and the logged-in check — see
    /// <see cref="CheckAccountViaWebFormAsync"/> / <see cref="RefreshStorageViaMyFilesAsync"/>.</summary>
    protected string MyFilesUrl => Host + "/?op=my_files";

    /// <summary>
    /// Which logged-in page the web-form account check and storage refresh read. The family default is
    /// the file manager, whose stock template carries both the storage bar and the account menu; forks
    /// that moved those elsewhere point this at their own page (Uploady keeps its storage figures on
    /// <c>my_account</c> and leaves <c>my_files</c> with none). Whatever page this names must carry the
    /// logout link, since it doubles as the still-signed-in probe.
    /// </summary>
    protected virtual string WebFormAccountPageUrl => MyFilesUrl;

    /// <summary>
    /// Reads (used, quota) out of the <see cref="WebFormAccountPageUrl"/> page. The default understands
    /// the stock <c>&lt;span class="storage"&gt;&lt;b&gt;X&lt;/b&gt; of &lt;b&gt;Y&lt;/b&gt;</c> bar;
    /// forks with a re-skinned dashboard override this. Either figure may come back null — callers
    /// treat "both null" as "nothing to report" and keep the previous snapshot.
    /// </summary>
    protected virtual (long? Used, long? Quota) ParseStorageUsage(string html) => TryParseStorageBar(html);

    /// <summary>
    /// Pulls the account name out of the <see cref="WebFormAccountPageUrl"/> page. The default reads the
    /// stock account menu (the token after the <c>fa-user</c> icon); forks that render the name
    /// elsewhere override this. Returning null is NOT harmless for a session-cookie hoster: those hide
    /// the username field entirely, so there is no typed value to fall back on and the account lands in
    /// the grid with a BLANK name, indistinguishable from any other account on that host.
    /// <para>
    /// <b>Check what the default actually matches before relying on it</b> — a theme that uses
    /// <c>fa-user</c> for something OTHER than the account menu makes it return a wrong name rather
    /// than none, which is harder to notice and worse to live with. Uploady renders that icon on its
    /// "Profile" tab and every account was saved as "Profile" until it overrode this.
    /// </para>
    /// </summary>
    protected virtual string? ParseAccountUsername(string html) => ExtractMyAccountUsername(html);

    /// <summary>
    /// Cookie lifetime applied during the U/P bootstrap window. XFileSharing rarely
    /// returns a real <c>Max-Age</c>; seven days matches the standard "remember me"
    /// horizon on the server side. Once bootstrap completes we throw the cookie away
    /// anyway, so this only matters when a user signs in via U/P but cancels the
    /// my_account scrape — the next attempt can re-use the cookie within this window.
    /// </summary>
    private static readonly TimeSpan DefaultCookieLifetime = TimeSpan.FromDays(7);

    // ---- Cloudflare managed-challenge clearance (opt-in; default off) ----
    //
    // Hosters whose whole domain sits behind a Cloudflare *managed challenge* (TakeFile) return the
    // "Just a moment…" interstitial to the C# HTTP stack — the WebView solved the challenge during
    // sign-in and holds a cf_clearance cookie, but the handler is a separate stack and doesn't. When
    // RequiresCloudflareClearance is on we (1) sign the WebView in with our handler's UA so the
    // issued clearance matches (SignInUserAgentOverride), (2) ALSO capture the cf_clearance cookie,
    // (3) store it combined with xfss so BuildCookieHeader forwards both on every request, and
    // (4) use a short session lifetime so we re-sign-in before the clearance (≈30 min) expires.
    // Everything is gated on the flag — classic XFS hosters are byte-for-byte unaffected.

    /// <summary>Override true for a hoster behind a Cloudflare managed challenge (see the block above).</summary>
    protected virtual bool RequiresCloudflareClearance => false;

    /// <summary>UA the sign-in WebView presents. Must equal the C# handler's UA so a captured
    /// cf_clearance is reusable. Null leaves the WebView2 default. Only consulted when
    /// <see cref="RequiresCloudflareClearance"/> is true.</summary>
    protected virtual string? SignInUserAgentOverride => null;

    /// <summary>Lifetime stamped on a freshly captured session. Defaults to the 7-day bootstrap
    /// window; cf_clearance-mode hosters shorten it so the stored session expires (forcing a
    /// re-sign-in that re-acquires clearance) before Cloudflare's clearance does.</summary>
    protected virtual TimeSpan SignInSessionLifetime => DefaultCookieLifetime;

    /// <summary>Cookie name Cloudflare uses for managed-challenge clearance.</summary>
    private const string CloudflareClearanceCookieName = "cf_clearance";

    /// <summary>Sign-in spec for the WebView. In cf_clearance mode it also captures the
    /// <c>cf_clearance</c> cookie and pins the browser UA so the clearance is reusable from C#.</summary>
    private InteractiveAuthSpec BuildSignInSpec()
        => RequiresCloudflareClearance
            ? new(Name, LoginUrl, CookieDomain, CookieName,
                AdditionalCookieNames: [CloudflareClearanceCookieName],
                UserAgentOverride: SignInUserAgentOverride,
                // Wait for the post-login navigation before capturing xfss: some XFS hosters (KatFile) set an
                // xfss guest cookie on the login page, which would otherwise close the window pre-authentication.
                CaptureOnlyAfterLeavingLoginPage: true)
            : new(Name, LoginUrl, CookieDomain, CookieName, CaptureOnlyAfterLeavingLoginPage: true);

    /// <summary>The value to persist as the session credential. Classic hosters store the bare
    /// <c>xfss</c> value; cf_clearance-mode stores a full <c>"xfss=…; cf_clearance=…"</c> Cookie
    /// header so <see cref="BuildCookieHeader"/> forwards both. Falls back to the bare value when
    /// the clearance cookie wasn't captured.</summary>
    private string ComposeStoredSession(InteractiveAuthResult result)
    {
        if (RequiresCloudflareClearance
            && result.AdditionalCookies is { } extra
            && extra.TryGetValue(CloudflareClearanceCookieName, out string? cf)
            && !string.IsNullOrEmpty(cf))
        {
            return $"{CookieName}={result.SessionCookieValue}; {CloudflareClearanceCookieName}={cf}";
        }

        return result.SessionCookieValue;
    }

    /// <summary>One bootstrap at a time per credentials id — prevents N parallel uploads
    /// on a brand-new account from all popping their own WebView.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _bootstrapGates = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<string>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    // Hidden-input regex for the CSRF token on the my_account page. Same shape every
    // XFileSharing variant renders for its `token` fields — handles attribute order
    // variation.
    private static readonly Regex _csrfTokenRegex = MyRegex();

    // The API key is rendered in one of four shapes across the XFileSharing family —
    // we accept all four:
    //   1. <input ... name="api-url" value="https://HOST/api/account/info?key=KEY">  (Ex-Load)
    //   2. <input value="...?key=KEY" ... name="api-url" ...>                         (reversed attr order)
    //   3. <span name="api-url">https://HOST/api/account/info?key=KEY</span>          (KatFile — key in text content, not an attribute)
    //   4. <input ... value="https://HOST/api/account/info?key=KEY">                 (Hexload — no name= attribute at all)
    // Branch 3 is the trickiest: anchor on `name="api-url"` followed by the closing `>`
    // of the element, then read up to the next `<` as the text node, and pluck `?key=...`
    // out of it. Branch 4 has no `name` to anchor on, so it anchors on the API-info URL
    // path (`/api/account/info?key=`) instead — specific enough not to match an unrelated
    // `?key=` value. The key character class excludes whitespace, &, ", ', <, and # so we
    // stop at the first delimiter the server would have escaped anyway.
    private static readonly Regex _apiKeyRegex = new(
        """name=["']api-url["'][^>]*?value=["'][^"']*[?&]key=([^"'&]+)["']""" +
        """|value=["'][^"']*[?&]key=([^"'&]+)["'][^>]*?name=["']api-url["']""" +
        """|name=["']api-url["'][^>]*>[^<]*?[?&]key=([^"'&<\s#]+)""" +
        """|value=["'][^"']*/api/account/info\?key=([^"'&]+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fifth shape (Hxfile, captured 2026-06-24): the key is rendered as a BARE token in the
    // my_account "API Key" table cell — not wrapped in a /api/account/info?key=… URL like the
    // four shapes above — sitting immediately before the "(Change key)" regenerate link
    // (<a … name="regen-api-key">). Anchor on that link and capture the token in front of it.
    // The negative lookbehind pins the capture to the token's true start, and the {12,} floor
    // keeps it from latching onto a short stray word. Consulted only as a fallback (see
    // ExtractApiKey), so the canonical api-url shapes always win; other XFS hosters that also
    // carry this regen link render the key as a URL first, so they never reach this branch.
    private static readonly Regex _apiKeyBareTokenRegex = new(
        """(?<![A-Za-z0-9])([A-Za-z0-9]{12,})\s*<a\b[^>]*\bname=["']regen-api-key["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Anonymous web upload (Hexload, captured 2026-06-13): the homepage renders
    //   <form id="uploadfile" action="https://<rand>.droply.top/cgi-bin/upload.cgi?upload_type=file&utype=anon">
    // The action host rotates per page load. Anchor on the upload.cgi action (the only form on
    // the page posting there) and capture it verbatim — query string included, since it carries
    // the upload_type/utype the backend expects.
    private static readonly Regex _anonUploadActionRegex = new(
        """<form\b[^>]*?\baction=["']([^"']*upload\.cgi[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Logged-in web-form upload (isra.cloud, captured 2026-06-26): the ?op=upload_form page renders
    //   <form id="uploadfile" action="https://fsNN.HOST/cgi-bin/upload.cgi?upload_type=file&utype=reg">
    //     <input type="hidden" name="sess_id" value="<session>">
    // _anonUploadActionRegex captures the action; this pulls the hidden sess_id. Note the action
    // scrape takes the FIRST upload.cgi form, which matters on pages carrying several — Uploady
    // renders the file uploader, then a remote-URL form, then a torrent form, all posting to some
    // upload.cgi, and only the first is the one we want. The value equals the xfss session-cookie value, but we read it from the
    // form so a server that mints a distinct per-session token is honoured verbatim. Handles either
    // attribute order (name-then-value / value-then-name).
    private static readonly Regex _sessIdInputRegex = new(
        """name=["']sess_id["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']sess_id["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // my_files storage bar scrape (web-form mode — isra.cloud renders the precise used + quota as
    //   <span class="storage"><b>705 KB</b> of <b>10.0 MB</b></span>).
    // The my_account "Used space" panel is useless for this (it shows TB-rounded usage and no cap —
    // "0.00 TB" for a 10 MB free account), so we read both figures from my_files instead. Anchor on
    // the storage span and capture used (value+unit) and total/quota (value+unit). Case-insensitive
    // so the lowercase "of" / any unit casing matches.
    // [^>]* after the class tolerates any further attributes on the span (e.g. a future id/title)
    // before its closing '>', matching the slack the api-url/username regexes above already allow.
    private static readonly Regex _storageBarRegex = new(
        """class=["']storage["'][^>]*>\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>\s*of\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The same bar in the "freespace" theme, which hangs it off id="occupied" instead of
    // class="storage": <span id="occupied"><b>5.0 MB</b> of <b>200.0 GB</b></span>. Tried only when
    // the stock anchor misses, so it can add figures but never change one. It lives here rather than
    // on a hoster because TWO now ship this theme (UpZur, BtaFile) with byte-identical markup, and a
    // third copy of one regex is how the copies start to disagree.
    private static readonly Regex _freeSpaceBarRegex = new(
        """id=["']occupied["'][^>]*>\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>\s*of\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The same pair again, in the plain my_account TABLE some forks use instead of either bar:
    // <TR><TD>Used space</TD><TD><b>0.00 of 500 GB</b></TD></TR>.
    // ⚠ The used figure carries NO unit of its own — it is stated in the quota's — which is why the
    // unit group here is optional and why neither bar pattern above can match this shape. Tried last,
    // so it can add figures but never change one. Here rather than on a hoster because TWO ship it
    // with identical markup (World Files, Xubster) and a second copy of one regex is how copies start
    // to disagree — the same reason the freespace bar moved down.
    private static readonly Regex _usedSpaceRowRegex = new(
        """<td[^>]*>\s*Used\s+space\s*</td>\s*<td[^>]*>\s*<b>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)?\s*of\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Username scrape (web-form mode): the account menu (on both my_account and my_files) renders the
    // username immediately after the user icon — <i class="fa fa-user"></i>pkjmq41030<i …>. Anchor on
    // that icon and capture the token in front of the next tag.
    private static readonly Regex _myAccountUsernameRegex = new(
        """fa-user\b[^>]*></i>\s*([A-Za-z0-9._@\-]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ...and the same name in the my_account table, for the forks whose theme has no fa-user icon at
    // all: <TR><TD>Username</TD><TD><b>NAME</b></TD></TR>. Tried only when the icon anchor misses.
    // Worth having in the base for the reason the icon anchor is fragile in the first place: with
    // neither, the account saves under whatever was typed, and what this app stores is what the next
    // sign-in POSTs. Two hosts ship this table (World Files, Xubster).
    private static readonly Regex _usernameRowRegex = new(
        """<td[^>]*>\s*Username\s*</td>\s*<td[^>]*>\s*<b>\s*([A-Za-z0-9._@\-]+)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Production ctor — supplied by DI with optional auth + repo.</summary>
    protected XFileSharingApiPipeline(IInteractiveAuthService? authService, FileHosterLoginRepository? loginRepository)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor — also accepts GET / upload overrides so the pipeline can be
    /// driven against canned responses without touching the network. Subclasses expose
    /// a matching internal ctor that delegates here.</summary>
    protected XFileSharingApiPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
        _postFormOverride = postFormOverride;
    }

    /// <summary>Stubs the login POST. Optional: only a <see cref="SupportsDirectLogin"/> hoster posts one.</summary>
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        if (MaxFileSizeFor(ctx.Credentials) is long maxBytes && ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds {Name}'s {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()})",
                null);
            yield break;
        }

        // Some hosts in this family refuse a file on grounds they only check at the FINAL step —
        // Uploadrar publishes an extension blocklist and enforces it in import_file, i.e. after the
        // entire file has been transferred. Asking here costs nothing and turns that into an instant
        // refusal instead of a wasted upload. See PreflightRejection.
        if (PreflightRejection(ctx) is { } preflightError)
        {
            yield return new AttemptFailed(preflightError, null);
            yield break;
        }

        // === Anonymous (not-logged-in) upload ===
        // When the hoster supports it and the attempt carries the wizard's anonymous
        // selection, skip the entire API-key flow and post to the web form's per-session
        // server. Other hosters / credentialed attempts fall through to the API path below.
        if (SupportsAnonymousUpload && ctx.Credentials.IsAnonymous)
        {
            await foreach (UploadEvent ev in RunAnonymousAsync(ctx, ct))
            {
                yield return ev;
            }
            yield break;
        }

        // === Web-form (no-API) logged-in upload ===
        // For hosters that don't expose the REST API, the upload server + sess_id come from the
        // logged-in ?op=upload_form page (authenticated by the xfss cookie) rather than
        // /api/upload/server; the rest reuses the classic multipart upload + ParseUploadResponse.
        if (UsesWebFormUpload)
        {
            await foreach (UploadEvent ev in RunWebFormAsync(ctx, ct))
            {
                yield return ev;
            }
            yield break;
        }

        // === Ensure we have an API key ===
        (string? apiKey, bool didBootstrap, string? authError) = await EnsureApiKeyAsync(ctx, ct);

        if (didBootstrap)
        {
            yield return new AuthStarted();
        }

        if (apiKey is null)
        {
            if (didBootstrap)
            {
                yield return new AuthFailed(authError ?? "could not obtain API key");
            }
            yield return new AttemptFailed(authError ?? "no API key available", null);
            yield break;
        }

        if (didBootstrap)
        {
            yield return new AuthSucceeded();
        }

        // === Resolve upload server ===
        (string? sessId, string? uploadUrl, string? serverError, bool serverAuthExpired) =
            await GetUploadServerAsync(apiKey, ctx, ct);

        if (serverAuthExpired)
        {
            // The API server rejected our key (user regenerated it elsewhere?). Clear and
            // force a re-bootstrap on the next attempt.
            await ClearApiKeyAsync(ctx.Credentials, ct).ConfigureAwait(false);
            yield return new AuthFailed("API key rejected — re-authenticate from Settings → Accounts");
            yield return new AttemptFailed("API key rejected — retry will re-authenticate", null);
            yield break;
        }

        if (sessId is null || uploadUrl is null)
        {
            yield return new AttemptFailed(serverError ?? "could not resolve upload server", null);
            yield break;
        }

        // === Upload ===
        // Looped for the same reason as the web-form path: a node that breaks AFTER taking the bytes
        // gets one retry against a freshly resolved server — see IsTransientNodeFailure. One
        // TransferStarted covers the whole thing; the retry is our business, not something the user
        // asked for or should see twice.
        string currentUploadUrl = uploadUrl;
        string currentSessId = sessId;
        bool retriedNodeFailure = false;

        yield return new TransferStarted(ctx.FileSize);

        while (true)
        {
            bool authExpiredDuringUpload = false;
            string? attemptFailure = null;
            bool attemptCancelled = false;
            Exception? attemptException = null;
            string? finalUrl = null;

            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, currentUploadUrl, currentSessId);

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

            HttpResponseSnapshot? uploadResponse = null;
            try
            {
                uploadResponse = await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                attemptCancelled = true;
            }
            catch (Exception ex)
            {
                attemptException = ex;
            }

            if (uploadResponse is not null)
            {
                (string? Url, string? Error, bool AuthExpired) = ParseUploadResponse(NormalizeUploadResponse(uploadResponse));
                if (AuthExpired)
                {
                    await ClearApiKeyAsync(ctx.Credentials, ct).ConfigureAwait(false);
                    authExpiredDuringUpload = true;
                }
                else if (Error is not null)
                {
                    attemptFailure = Error;
                }
                else
                {
                    finalUrl = Url;
                }
            }

            if (authExpiredDuringUpload)
            {
                yield return new AuthFailed("API key rejected mid-upload");
                yield return new AttemptFailed("API key rejected — retry will re-authenticate", null);
                yield break;
            }

            if (attemptCancelled)
            {
                yield return new AttemptCancelled();
                yield break;
            }

            if (attemptException is not null)
            {
                yield return new AttemptFailed(attemptException.Message, attemptException);
                yield break;
            }

            if (attemptFailure is not null)
            {
                if (retriedNodeFailure || !IsTransientNodeFailure(attemptFailure))
                {
                    yield return new AttemptFailed(attemptFailure, null);
                    yield break;
                }

                retriedNodeFailure = true;
                ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node reported a backend failure ({attemptFailure}); retrying once with a fresh server.");

                // Re-ask the API for a server: it hands out the node, so this is what makes the retry
                // land somewhere else rather than repeat itself against the one that just failed.
                (string? retrySessId, string? retryUrl, string? retryError, bool retryAuthExpired) =
                    await GetUploadServerAsync(apiKey, ctx, ct);

                if (retryAuthExpired)
                {
                    await ClearApiKeyAsync(ctx.Credentials, ct).ConfigureAwait(false);
                    yield return new AuthFailed("API key rejected — re-authenticate from Settings → Accounts");
                    yield return new AttemptFailed("API key rejected — retry will re-authenticate", null);
                    yield break;
                }

                if (retryUrl is null || retrySessId is null)
                {
                    // Report the node's own failure — the reason we were retrying at all — rather than
                    // the re-resolve's, which is a symptom of it.
                    _ = retryError;
                    yield return new AttemptFailed(attemptFailure, null);
                    yield break;
                }

                currentUploadUrl = retryUrl;
                currentSessId = retrySessId;
                continue;
            }

            if (finalUrl is not null)
            {
                yield return new TransferCompleted(finalUrl);
            }

            yield break;
        }
    }
}
