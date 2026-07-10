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
/// </remarks>
public abstract partial class XFileSharingApiPipeline : IFileHosterPipeline
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

    /// <inheritdoc/>
    public bool RequiresHashingBeforeUpload => false;

    /// <inheritdoc/>
    public bool RequiresHashingAfterUpload => false;

    // ---- Derived URLs ----

    protected string LoginUrl => Host + LoginPagePath;
    protected string MyAccountUrl => Host + "/?op=my_account";
    protected string PublicUrlPrefix => Host + "/";
    protected string ApiAccountInfoUrl => Host + "/api/account/info";
    protected string ApiUploadServerUrl => Host + "/api/upload/server";

    /// <summary>The logged-in web upload form (web-form mode only). Carries the per-session
    /// upload-server <c>action</c> and the hidden <c>sess_id</c> we scrape in
    /// <see cref="GetWebFormUploadServerAsync"/>.</summary>
    protected string UploadFormUrl => Host + "/?op=upload_form";

    /// <summary>The logged-in file manager (web-form mode only). Source of the account's storage bar
    /// (<c>used of total</c>), the username, and the logged-in check — see
    /// <see cref="CheckAccountViaWebFormAsync"/> / <see cref="RefreshStorageViaMyFilesAsync"/>.</summary>
    protected string MyFilesUrl => Host + "/?op=my_files";

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
                UserAgentOverride: SignInUserAgentOverride)
            : new(Name, LoginUrl, CookieDomain, CookieName);

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
    // _anonUploadActionRegex captures the action (the only upload.cgi form on the page); this pulls
    // the hidden sess_id. The value equals the xfss session-cookie value, but we read it from the
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

    // Username scrape (web-form mode): the account menu (on both my_account and my_files) renders the
    // username immediately after the user icon — <i class="fa fa-user"></i>pkjmq41030<i …>. Anchor on
    // that icon and capture the token in front of the next tag.
    private static readonly Regex _myAccountUsernameRegex = new(
        """fa-user\b[^>]*></i>\s*([A-Za-z0-9._@\-]+)""",
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
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _authService = authService;
        _loginRepository = loginRepository;
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
    }

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
        bool authExpiredDuringUpload = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadUrl, sessId);

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
            (string? Url, string? Error, bool AuthExpired) = ParseUploadResponse(uploadResponse);
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
            yield return new AttemptFailed(attemptFailure, null);
            yield break;
        }

        if (finalUrl is not null)
        {
            yield return new TransferCompleted(finalUrl);
        }
    }

    /// <summary>
    /// Anonymous upload path for hosters that set <see cref="SupportsAnonymousUpload"/>. No
    /// API, no login: GET the web upload form to discover the per-session upload server, POST
    /// the file exactly as the browser's anonymous form does (empty <c>sess_id</c>,
    /// <c>utype=anon</c>), then parse the same <c>[{file_code, file_status}]</c> JSON the API
    /// path returns. Mirrors <see cref="RunAsync"/>'s upload/progress/parse machinery.
    /// </summary>
    /// <remarks>
    /// The homepage hands out a rotating upload server and some assignments resolve to dead CDN
    /// domains (observed: hexload.com served an unresolvable <c>*.drewimplemnt.top</c> while a
    /// retry got a live <c>*.droply.top</c>). On a connection/DNS failure — which happens before
    /// any bytes are sent, so nothing is wasted — we re-fetch a fresh server and retry, bounded
    /// by <see cref="AnonymousServerAttempts"/>.
    /// </remarks>
    private async IAsyncEnumerable<UploadEvent> RunAnonymousAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TransferStarted(ctx.FileSize);

        HttpRequestException? lastUnreachable = null;

        for (int attempt = 0; attempt < AnonymousServerAttempts; attempt++)
        {
            (string? uploadUrl, string? discoverError) = await DiscoverAnonymousServerAsync(ctx, ct);
            if (uploadUrl is null)
            {
                yield return new AttemptFailed(discoverError!, null);
                yield break;
            }

            // Progress bridge (same pattern as the API path).
            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = AnonymousUploadAsync(ctx, uploadUrl);

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

            bool cancelled = false;
            bool unreachable = false;
            Exception? exception = null;
            HttpResponseSnapshot? response = null;
            try
            {
                response = await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (HttpRequestException hre) when (IsServerUnreachable(hre))
            {
                // The assigned upload server didn't resolve/connect — no bytes were sent, so
                // grabbing a fresh server and retrying wastes nothing.
                lastUnreachable = hre;
                unreachable = true;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (cancelled)
            {
                yield return new AttemptCancelled();
                yield break;
            }

            if (unreachable)
            {
                if (attempt < AnonymousServerAttempts - 1)
                {
                    ctx.Logger.Log(this, LogType.Status, $"{Name}: anonymous upload server unreachable ({lastUnreachable!.Message}); retrying with a fresh server.");
                    continue;
                }

                yield return new AttemptFailed(
                    $"{Name}: anonymous upload servers were unreachable after {AnonymousServerAttempts} attempts (last: {lastUnreachable!.Message}). "
                    + "The hoster rotates upload servers and handed out unresolvable ones — try again.",
                    lastUnreachable);
                yield break;
            }

            if (exception is not null)
            {
                yield return new AttemptFailed(exception.Message, exception);
                yield break;
            }

            (string? url, string? error, bool _) = ParseUploadResponse(response!);
            if (url is not null)
            {
                yield return new TransferCompleted(url);
            }
            else
            {
                yield return new AttemptFailed(error ?? $"{Name}: anonymous upload returned no download link", null);
            }

            yield break;
        }
    }

    /// <summary>
    /// Fresh-server attempts for an anonymous upload before giving up. The homepage rotates the
    /// assigned upload server and a large share are dead (resolve to 0.0.0.0 / NODATA — observed
    /// ~half on hexload.com), so each attempt re-fetches a cache-busted homepage for a different
    /// server. Five tries makes hitting a live one near-certain while wasting nothing — dead
    /// servers fail at DNS/connect, before any bytes are sent.
    /// </summary>
    private const int AnonymousServerAttempts = 5;

    /// <summary>Sent on the homepage GET alongside the cache-buster query — belt-and-suspenders
    /// against any intermediary that honours request no-cache.</summary>
    private static readonly Dictionary<string, string> NoCacheHeaders = new(StringComparer.Ordinal)
    {
        ["Cache-Control"] = "no-cache",
        ["Pragma"] = "no-cache",
    };

    /// <summary>
    /// GETs the web upload form and scrapes the per-session upload server's <c>action</c> URL
    /// (the rotating <c>…/cgi-bin/upload.cgi?…</c>). Returns (null, error) when the homepage
    /// fetch fails or no upload form is present.
    /// </summary>
    private async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        // Cache-bust the homepage: it's cached per-connection/edge, so a plain re-GET of "/"
        // hands back the SAME (often dead) upload server — defeating the retry. A unique query
        // param forces a fresh assignment so each attempt actually tries a different server.
        string url = $"{Host}/?_={Guid.NewGuid():N}";

        string html;
        try
        {
            html = await GetAsync(ctx, url, NoCacheHeaders, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: anonymous upload form fetch failed: {ex.Message}");
        }

        Match m = _anonUploadActionRegex.Match(html);
        return m.Success
            ? (m.Groups[1].Value, null)
            : (null, $"{Name}: anonymous upload form (a <form action=\"…/upload.cgi…\">) not found on the homepage");
    }

    /// <summary>
    /// True when an upload POST failed because the server couldn't be reached at all — DNS
    /// resolution or TCP connect failed, i.e. before any bytes were sent. Safe to retry against
    /// a freshly-assigned server. A mid-stream failure (bytes already in flight) is NOT this and
    /// is surfaced as a normal failure so a partially-uploaded file is never re-sent.
    /// </summary>
    private static bool IsServerUnreachable(HttpRequestException ex)
        => ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
           || ex.InnerException is System.Net.Sockets.SocketException;

    private Task<HttpResponseSnapshot> AnonymousUploadAsync(AttemptContext ctx, string uploadUrl)
    {
        Dictionary<string, string> fields = BuildAnonymousExtraFields();
        Dictionary<string, string> headers = BrowserAnonymousHeaders();

        if (_uploadOverride is not null)
        {
            return _uploadOverride(ctx.FilePath, uploadUrl, fields, headers, ctx.SpeedLimitProvider);
        }

        return ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "file_0",
            extraFields: fields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Exact field set the browser posts for an anonymous upload (captured from hexload.com
    /// 2026-06-13, in this order): <c>utype=anon</c> + an empty <c>sess_id</c> are what
    /// distinguish it from the logged-in classic POST. The empties must be present — the
    /// XFileSharing multipart parser is field-presence sensitive (see brupload-multipart-quirks).
    /// </summary>
    private static Dictionary<string, string> BuildAnonymousExtraFields() => new(StringComparer.Ordinal)
    {
        ["sess_id"] = string.Empty,
        ["utype"] = "anon",
        ["mode"] = string.Empty,
        ["file_public"] = string.Empty,
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Headers for the anonymous upload POST. Cross-site (the upload server is a different
    /// registered domain than the apex — e.g. <c>droply.top</c> for <c>hexload.com</c>), with
    /// Referer, matching the browser capture. No Cookie: the anonymous POST carries no session.
    /// </summary>
    private Dictionary<string, string> BrowserAnonymousHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Sec-Fetch-Site"] = "cross-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
    };

    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> EnsureApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
        {
            return (ctx.Credentials.ApiKey, false, null);
        }

        SemaphoreSlim gate = _bootstrapGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
            {
                return (ctx.Credentials.ApiKey, false, null);
            }

            if (string.IsNullOrEmpty(ctx.Credentials.Username))
            {
                return (null, false, "no API key set and no username supplied — open Settings → Accounts and either paste an API key or sign in with username/password");
            }

            return await BootstrapApiKeyAsync(ctx, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> BootstrapApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        string? xfss = await GetOrAcquireXfssCookieAsync(ctx, ct);
        if (xfss is null)
        {
            return (null, true, "sign-in cancelled or no usable proxy available");
        }

        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(xfss);
        string html;
        try
        {
            html = await GetAsync(ctx, MyAccountUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return (null, true, "my_account fetch failed: " + ex.Message);
        }

        string? apiKey = ExtractApiKey(html);

        if (apiKey is null)
        {
            string? csrf = ExtractCsrfToken(html);
            if (csrf is null)
            {
                return (null, true, "my_account did not contain an API key OR a CSRF token to generate one. " + Snippet(html));
            }

            string generateUrl = $"{MyAccountUrl}&generate_api_key=1&token={Uri.EscapeDataString(csrf)}";
            try
            {
                _ = await GetAsync(ctx, generateUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return (null, true, "generate_api_key request failed: " + ex.Message);
            }

            try
            {
                html = await GetAsync(ctx, MyAccountUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return (null, true, "my_account re-fetch failed after generate: " + ex.Message);
            }

            apiKey = ExtractApiKey(html);
            if (apiKey is null)
            {
                return (null, true, "my_account did not contain an api-url input after generate. " + Snippet(html));
            }
        }

        await PersistApiKeyAsync(ctx.Credentials, apiKey, ct).ConfigureAwait(false);

        ctx.Logger.Log(this, LogType.Status, $"{Name}: bootstrapped API key for {ctx.Credentials.Username}");
        return (apiKey, true, null);
    }

    private async Task<string?> GetOrAcquireXfssCookieAsync(AttemptContext ctx, CancellationToken ct)
    {
        bool pinMatches = ctx.Credentials.PinnedProxyId is null || ctx.Credentials.PinnedProxyId == ctx.Proxy.Id;

        if (pinMatches
            && !string.IsNullOrEmpty(ctx.Credentials.SessionCookie)
            && ctx.Credentials.SessionCookieExpiresUtc is DateTime expiresUtc
            && expiresUtc > DateTime.UtcNow)
        {
            return ctx.Credentials.SessionCookie;
        }

        if (_authService is null)
        {
            return null;
        }

        // UsernameCookieName: null — XFileSharing-family hosters don't put the identity
        // in the cookie jar; their /api/account/info endpoint returns the email instead.
        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(
                BuildSignInSpec(),
                ctx.Credentials.Username ?? string.Empty,
                ctx.Proxy,
                ct);
        }
        catch
        {
            return null;
        }

        if (captured is not InteractiveAuthResult result)
        {
            return null;
        }

        string stored = ComposeStoredSession(result);
        ctx.Credentials.SessionCookie = stored;
        ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + SignInSessionLifetime;
        ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

        if (_loginRepository is not null)
        {
            await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
        }

        return stored;
    }

    // ======== Web-form (no-API) path ========

    /// <summary>
    /// Web-form (no-API) logged-in upload. Mirrors <see cref="RunAsync"/>'s upload/progress/parse
    /// machinery but resolves the upload server from the logged-in <c>?op=upload_form</c> page
    /// (scraping the form <c>action</c> + hidden <c>sess_id</c>) instead of <c>/api/upload/server</c>,
    /// and authenticates with the <c>xfss</c> session cookie (no API key). Auth-expiry — the upload
    /// form bounced us to the login page, or the upload itself returned Unauthorized — clears the
    /// stored cookie so the next attempt re-signs-in.
    /// </summary>
    private async IAsyncEnumerable<UploadEvent> RunWebFormAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Ensure we have a session cookie (sign in via WebView only if we don't) ===
        bool needSignIn = !HasValidStoredSessionCookie(ctx);
        if (needSignIn)
        {
            yield return new AuthStarted();
        }

        string? xfss = await GetOrAcquireXfssCookieAsync(ctx, ct);
        if (xfss is null)
        {
            if (needSignIn)
            {
                yield return new AuthFailed("sign-in cancelled or no usable proxy available");
            }
            yield return new AttemptFailed("not signed in — open Settings → Accounts and sign in", null);
            yield break;
        }

        if (needSignIn)
        {
            yield return new AuthSucceeded();
        }

        // === Resolve the upload server from the logged-in upload form ===
        (string? uploadUrl, string? sessId, string? serverError, bool serverAuthExpired) =
            await GetWebFormUploadServerAsync(ctx, xfss, ct);

        if (serverAuthExpired)
        {
            await ClearSessionCookieAsync(ctx.Credentials, ct).ConfigureAwait(false);
            yield return new AuthFailed("session expired — sign in again from Settings → Accounts");
            yield return new AttemptFailed("session expired — retry will re-authenticate", null);
            yield break;
        }

        if (uploadUrl is null || sessId is null)
        {
            yield return new AttemptFailed(serverError ?? "could not resolve the upload server", null);
            yield break;
        }

        // === Upload (identical machinery to the API path) ===
        bool authExpiredDuringUpload = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadUrl, sessId);

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
            (string? Url, string? Error, bool AuthExpired) = ParseUploadResponse(uploadResponse);
            if (AuthExpired)
            {
                await ClearSessionCookieAsync(ctx.Credentials, ct).ConfigureAwait(false);
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
            yield return new AuthFailed("session expired mid-upload");
            yield return new AttemptFailed("session expired — retry will re-authenticate", null);
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
            yield return new AttemptFailed(attemptFailure, null);
            yield break;
        }

        if (finalUrl is not null)
        {
            yield return new TransferCompleted(finalUrl);
        }
    }

    /// <summary>
    /// GETs the logged-in <c>?op=upload_form</c> page (with the <c>xfss</c> cookie) and scrapes the
    /// per-session upload server's <c>action</c> URL (<c>fsNN/cgi-bin/upload.cgi?…</c>) + the hidden
    /// <c>sess_id</c>. A page with no upload form means the cookie no longer authenticates us (the
    /// server served a logged-out / login page) → reported as auth-expired so the caller clears the
    /// cookie and re-signs-in. Falls back to the cookie value for <c>sess_id</c> when the form omits
    /// the hidden input (it equals the cookie in the capture).
    /// </summary>
    private async Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> GetWebFormUploadServerAsync(
        AttemptContext ctx, string xfss, CancellationToken ct)
    {
        string html;
        try
        {
            html = await GetAsync(ctx, UploadFormUrl, BuildCookieHeader(xfss), ct);
        }
        catch (Exception ex)
        {
            return (null, null, "upload_form fetch failed: " + ex.Message, false);
        }

        Match action = _anonUploadActionRegex.Match(html);
        if (!action.Success)
        {
            return (null, null, "upload form not found — the session may have expired", true);
        }

        Match sess = _sessIdInputRegex.Match(html);
        string sessId = sess.Success
            ? (sess.Groups[1].Success && sess.Groups[1].Length > 0 ? sess.Groups[1].Value : sess.Groups[2].Value)
            : string.Empty;
        if (string.IsNullOrEmpty(sessId))
        {
            sessId = xfss;
        }

        return (action.Groups[1].Value, sessId, null, false);
    }

    /// <summary>Mirror of the cookie-validity check inside <see cref="GetOrAcquireXfssCookieAsync"/>:
    /// true when a non-expired session cookie pinned to (or unpinned from) the current proxy is on the
    /// DTO — i.e. when no WebView pop is needed. Lets <see cref="RunWebFormAsync"/> emit the Auth*
    /// events only when a sign-in actually happens.</summary>
    private static bool HasValidStoredSessionCookie(AttemptContext ctx)
    {
        bool pinMatches = ctx.Credentials.PinnedProxyId is null || ctx.Credentials.PinnedProxyId == ctx.Proxy.Id;
        return pinMatches
            && !string.IsNullOrEmpty(ctx.Credentials.SessionCookie)
            && ctx.Credentials.SessionCookieExpiresUtc is DateTime expiresUtc
            && expiresUtc > DateTime.UtcNow;
    }

    private async Task ClearSessionCookieAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Account verification for web-form (no-API) hosters: WebView sign-in to capture the <c>xfss</c>
    /// cookie, then a <c>my_account</c> HTML scrape for logged-in confirmation, the username, and
    /// storage usage. No API key is involved; the persisted credential is the session cookie (reused
    /// by <see cref="RunWebFormAsync"/> and by the non-interactive storage refresh). Quota is always
    /// null — these hosters don't advertise a cap, so the grid's Available cell shows "Unlimited".
    /// </summary>
    private async Task<AccountCheckResult> CheckAccountViaWebFormAsync(string username, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in service unavailable. Restart the app and try again.");
        }

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(BuildSignInSpec(), username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (captured is not InteractiveAuthResult auth)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled.");
        }

        string storedSession = ComposeStoredSession(auth);
        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(storedSession);
        string html;
        string finalUrl;
        int hops;
        try
        {
            (html, finalUrl, hops) = await FetchMyAccountAsync(handler, MyFilesUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "my_files fetch failed: " + ex.Message);
        }

        if (!LooksLoggedIn(html))
        {
            string trail = hops > 0 ? $" after following {hops} redirect(s) to {finalUrl}" : string.Empty;
            string summary = $"Signed in, but the file manager didn't load as logged-in{trail}. The sign-in may not have completed.";
            return new AccountCheckResult(false, AccountType.Free, summary, Detail: BuildFailureDetail(summary, html));
        }

        string? scrapedUsername = ExtractMyAccountUsername(html);
        (long? used, long? quota) = TryParseStorageBar(html);

        return new AccountCheckResult(
            IsValid: true,
            AccountType: AccountType.Free,
            Message: "Signed in (Free)",
            SessionCookie: storedSession,
            SessionCookieExpiresUtc: DateTime.UtcNow + SignInSessionLifetime,
            PinnedProxyId: proxy.Id,
            DerivedUsername: scrapedUsername ?? (string.IsNullOrEmpty(username) ? null : username),
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>
    /// Non-interactive storage refresh for web-form hosters: GET <c>my_files</c> with the STORED
    /// <c>xfss</c> cookie (never a WebView) and scrape the storage bar (used + quota). Returns null
    /// when there's no usable stored cookie, the fetch fails, the page isn't logged-in, or neither
    /// figure parsed — callers keep the last-known snapshot. Subclasses that implement
    /// <see cref="IStorageRefreshablePipeline"/> delegate here.
    /// </summary>
    protected async Task<StorageUsage?> RefreshStorageViaMyFilesAsync(
        FileHosterLoginDto credentials, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // the handler already routes through the chosen proxy.

        if (string.IsNullOrEmpty(credentials.SessionCookie))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(credentials.SessionCookie);
        string html;
        try
        {
            (html, _, _) = await FetchMyAccountAsync(handler, MyFilesUrl, cookieHeader, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (!LooksLoggedIn(html))
        {
            return null;
        }

        (long? used, long? quota) = TryParseStorageBar(html);
        return used is null && quota is null ? null : new StorageUsage(used, quota);
    }

    /// <summary>True when a fetched logged-in page (<c>my_account</c> / <c>my_files</c>) carries a
    /// logout link. A logged-out fetch lands on the login page, which has none.</summary>
    private static bool LooksLoggedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractMyAccountUsername(string html)
    {
        Match m = _myAccountUsernameRegex.Match(html);
        return m.Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Parses the <c>my_files</c> storage bar (<c>used of total</c>) into (usedBytes, quotaBytes),
    /// using binary (IEC) multipliers to match the app's storage display. Either may be null when its
    /// figure is absent/unparseable; both null when the bar isn't present. Internal for direct unit
    /// testing.
    /// </summary>
    internal static (long? Used, long? Quota) TryParseStorageBar(string html)
    {
        Match m = _storageBarRegex.Match(html);
        if (!m.Success)
        {
            return (null, null);
        }

        return (ParseSizeToBytes(m.Groups[1].Value, m.Groups[2].Value),
                ParseSizeToBytes(m.Groups[3].Value, m.Groups[4].Value));
    }

    /// <summary>Converts a scraped size figure (e.g. number "10.0", unit "MB") to bytes using binary
    /// (IEC) multipliers, tolerating a comma decimal separator. Returns null when unparseable.</summary>
    internal static long? ParseSizeToBytes(string number, string unit)
    {
        string num = number.Replace(',', '.');
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || value < 0)
        {
            return null;
        }

        long multiplier = unit.ToUpperInvariant() switch
        {
            "TB" => 1L << 40,
            "GB" => 1L << 30,
            "MB" => 1L << 20,
            "KB" => 1L << 10,
            "B" => 1L,
            _ => 0L,
        };

        return multiplier == 0L ? null : (long)(value * multiplier);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = password; // XFileSharing API-mode doesn't validate the password — sign-in goes through the WebView captcha.

        // Web-form (no-API) hosters: there's no API key to validate and no /api/account/info to call.
        // Sign in via WebView and read identity/storage from the my_files HTML instead.
        if (UsesWebFormUpload)
        {
            return await CheckAccountViaWebFormAsync(username, handler, proxy, ct);
        }

        // API-key-direct path: validate via /api/account/info and surface premium expiry.
        if (!string.IsNullOrEmpty(apiKey))
        {
            AccountInfo? info = await TryGetAccountInfoAsync(apiKey, handler, ct);
            if (info is null)
            {
                return new AccountCheckResult(false, AccountType.Free, "API key was rejected by /api/account/info or the response was unreadable.");
            }

            (AccountType accountType, DateTime? expiry) = ClassifyPremium(info);
            string message = expiry is DateTime e && accountType == AccountType.Premium
                ? $"Premium until {e:yyyy-MM-dd}"
                : "Free account";

            // Storage comes straight from the /api/account/info JSON (storage_used +
            // storage_left), so the api-key path needs no cookie / HTML scrape. "inf"
            // storage_left → quota null → grid's Available cell renders blank.
            (long? apiUsed, long? apiQuota) = ParseStorageFromAccountInfo(info);

            return new AccountCheckResult(
                IsValid: true,
                AccountType: accountType,
                Message: message,
                PremiumExpiry: expiry,
                ApiKey: apiKey,
                // Surface the email so Settings VM can fill an empty Username column on
                // API-key-direct accounts (the user pasted a key with no email; the grid
                // would otherwise show a blank cell).
                DerivedUsername: info.Email,
                StorageUsedBytes: apiUsed,
                StorageQuotaBytes: apiQuota);
        }

        // U/P mode — bootstrap an API key via WebView + my_account scrape.
        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in service unavailable. Restart the app and try again.");
        }

        InteractiveAuthResult? captured;
        try
        {
            // UsernameCookieName: null — XFS identity comes from /api/account/info, not a cookie.
            captured = await _authService.AcquireSessionCookieAsync(BuildSignInSpec(), username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (captured is not InteractiveAuthResult auth)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled.");
        }

        string storedSession = ComposeStoredSession(auth);
        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(storedSession);
        string html;
        string finalUrl;
        int hops;
        try
        {
            (html, finalUrl, hops) = await FetchMyAccountAsync(handler, MyAccountUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "my_account fetch failed: " + ex.Message);
        }

        // Local rename to avoid shadowing the apiKey parameter.
        string? derivedKey = ExtractApiKey(html);
        if (derivedKey is null)
        {
            string? csrf = ExtractCsrfToken(html);
            if (csrf is null)
            {
                // Surface the redirect trail so a future failure of this shape points
                // at "we landed somewhere wrong" vs. "the live HTML changed shape".
                // ex-load.com's 302→login interstitial is the classic case (was caught
                // by adding the redirect-follow here in the first place).
                string trail = hops > 0 ? $" after following {hops} redirect(s) to {finalUrl}" : string.Empty;
                string summary = $"my_account did not contain an API key OR a CSRF token{trail}. The sign-in may not have worked.";
                // Message stays short (grid/status text); the full response goes into Detail so
                // the Add Account "Details" dialog can show the complete page, not a 200-char snippet.
                return new AccountCheckResult(false, AccountType.Free, summary, Detail: BuildFailureDetail(summary, html));
            }

            string generateUrl = $"{MyAccountUrl}&generate_api_key=1&token={Uri.EscapeDataString(csrf)}";
            try
            {
                _ = await FetchMyAccountAsync(handler, generateUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return new AccountCheckResult(false, AccountType.Free, "generate_api_key request failed: " + ex.Message);
            }

            try
            {
                (html, finalUrl, hops) = await FetchMyAccountAsync(handler, MyAccountUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return new AccountCheckResult(false, AccountType.Free, "my_account re-fetch failed: " + ex.Message);
            }

            derivedKey = ExtractApiKey(html);
            if (derivedKey is null)
            {
                string trail = hops > 0 ? $" after following {hops} redirect(s) to {finalUrl}" : string.Empty;
                string summary = $"my_account did not contain an api-url input after generate{trail}.";
                return new AccountCheckResult(false, AccountType.Free, summary, Detail: BuildFailureDetail(summary, html));
            }
        }

        AccountInfo? derivedInfo = await TryGetAccountInfoAsync(derivedKey, handler, ct);
        AccountType derivedType = AccountType.Free;
        string derivedMessage;
        if (derivedInfo is null)
        {
            derivedMessage = "API key obtained but account/info verification failed.";
        }
        else
        {
            (derivedType, DateTime? expiry) = ClassifyPremium(derivedInfo);
            derivedMessage = expiry is DateTime e && derivedType == AccountType.Premium
                ? $"Premium until {e:yyyy-MM-dd}"
                : "Signed in (Free)";
        }

        // Storage comes from the same /api/account/info JSON we already fetched to derive
        // the key — storage_used + storage_left. "inf" → quota null → Available blank.
        (long? storageUsed, long? storageQuota) = derivedInfo is null
            ? (null, null)
            : ParseStorageFromAccountInfo(derivedInfo);

        return new AccountCheckResult(
            IsValid: true,
            AccountType: derivedType,
            Message: derivedMessage,
            PremiumExpiry: derivedInfo is null ? null : ClassifyPremium(derivedInfo).Expiry,
            // Persist the freshly captured session too — RunAsync's upload path reuses it, and a
            // refresh can reuse it without re-popping the WebView. In cf_clearance mode this carries
            // the combined xfss+cf_clearance header and a shorter lifetime (so it re-signs-in before
            // the clearance expires); classic mode stores the bare xfss with the 7-day window.
            SessionCookie: storedSession,
            SessionCookieExpiresUtc: DateTime.UtcNow + SignInSessionLifetime,
            PinnedProxyId: proxy.Id,
            ApiKey: derivedKey,
            DerivedUsername: derivedInfo?.Email,
            StorageUsedBytes: storageUsed,
            StorageQuotaBytes: storageQuota);
    }

    private async Task<(string? SessId, string? UploadUrl, string? Error, bool AuthExpired)> GetUploadServerAsync(string apiKey, AttemptContext ctx, CancellationToken ct)
    {
        string url = $"{ApiUploadServerUrl}?key={Uri.EscapeDataString(apiKey)}";
        string body;
        try
        {
            body = await GetAsync(ctx, url, headers: null, ct);
        }
        catch (Exception ex)
        {
            return (null, null, "upload/server request failed: " + ex.Message, false);
        }

        UploadServerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<UploadServerResponse>(body);
        }
        catch
        {
            return (null, null, $"upload/server: response was not valid JSON: {Snippet(body)}", false);
        }

        if (response is null)
        {
            return (null, null, $"upload/server: empty response: {Snippet(body)}", false);
        }

        if (response.Status == 403 || response.Status == 401)
        {
            return (null, null, response.Msg ?? "API key rejected", true);
        }

        if (response.Status != 200 || string.IsNullOrEmpty(response.Result) || string.IsNullOrEmpty(response.SessId))
        {
            return (null, null, $"upload/server: status={response.Status} msg={response.Msg}", false);
        }

        return (response.SessId, NormaliseUploadUrlScheme(response.Result), null, false);
    }

    /// <summary>
    /// Whether to downgrade an <c>https://</c> upload-server URL (whose host differs from the
    /// API host) to <c>http</c>. Default <c>false</c> — RESPECT the scheme the API returned.
    /// Only hosters whose storage subdomain serves a broken cert on :443 (but HTTP/1.1 cleanly
    /// on :80) opt in by overriding this to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Some XFileSharingPro hosters serve per-user storage subdomains on shared infra that
    /// listens on :443 with a junk certificate — observed on FlashBit's <c>fs1.flashbit.cc</c>,
    /// where :443 presents a self-signed cert for <c>srv1.pusula.co</c> so the TLS handshake
    /// fails before the first body byte; the same subdomain answers HTTP/1.1 on :80 cleanly,
    /// and the only credential is the sess_id in the request body (nothing rides the transport
    /// that TLS protects), so HTTP is safe THERE. Such hosters set this true.
    /// <para>
    /// The default is the opposite — respect the API's scheme — because the upload server tells
    /// us which scheme it serves and overriding that is usually WRONG. Hexload's rotating
    /// <c>*.droply.top</c>/<c>*.drewimplemnt.top</c> servers carry a valid Let's Encrypt cert
    /// and REQUIRE https: over http they 301 to https, and for bodies past ~1 KB they emit that
    /// 301 before reading the body, half-closing the socket on a streaming client mid-upload
    /// (SocketException 10054). So we never downgrade unless a subclass explicitly opts in.
    /// </para>
    /// </remarks>
    protected virtual bool DowngradeUploadServerToHttp => false;

    /// <summary>
    /// Honours <see cref="DowngradeUploadServerToHttp"/>: when set, rewrites an <c>https://</c>
    /// upload URL whose host differs from the API host to <c>http</c>; otherwise returns the URL
    /// unchanged. A URL pointing back at the API host always stays as-given.
    /// </summary>
    private string NormaliseUploadUrlScheme(string uploadUrl)
    {
        if (!DowngradeUploadServerToHttp)
        {
            return uploadUrl;
        }

        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uploadUri))
        {
            return uploadUrl;
        }
        if (uploadUri.Scheme != Uri.UriSchemeHttps)
        {
            return uploadUrl;
        }
        if (!Uri.TryCreate(Host, UriKind.Absolute, out Uri? apiUri))
        {
            return uploadUrl;
        }
        if (string.Equals(uploadUri.Host, apiUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return uploadUrl;
        }
        UriBuilder b = new(uploadUri) { Scheme = Uri.UriSchemeHttp };
        // UriBuilder defaults the port to the new scheme's default (80) only when the
        // original URL didn't carry an explicit port — that's exactly the behaviour
        // we want here. If the API ever returns an explicit port we preserve it.
        if (uploadUri.IsDefaultPort)
        {
            b.Port = -1;
        }
        return b.Uri.ToString();
    }

    /// <summary>
    /// Browser-shaped headers for the classic single-multipart <c>upload.cgi</c> POST.
    /// Sec-Fetch-Site is <c>same-site</c> because classic XFileSharing keeps the upload
    /// on a subdomain of the apex (e.g. <c>fs40.ex-load.com</c>) — the BRupload-era
    /// shape that proven-working hosters expect.
    /// </summary>
    private Dictionary<string, string> BrowserClassicHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Sec-Fetch-Site"] = "same-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
    };

    /// <summary>
    /// Browser-shaped headers for the chunked <c>up.cgi</c> / <c>api.cgi</c> POSTs.
    /// Sec-Fetch-Site is <c>cross-site</c> because the modern XFileSharing CDN backends
    /// live on a different registered domain than the apex (e.g. <c>ctmp.world</c> for
    /// hxfile.co). Referer is included to match the browser capture; some XFS CDN
    /// fronts reject preflight-less POSTs without it.
    /// </summary>
    private Dictionary<string, string> BrowserChunkedHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Sec-Fetch-Site"] = "cross-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
        ["Referer"] = Host + "/",
    };

    /// <summary>
    /// Initial chunk size for the modern XFileSharing chunked protocol. 80 MiB is hard-
    /// coded in the upload-chunked.js loaded by hxfile.co (and is what their CDN
    /// frontends expect). We start here for maximum throughput; if chunk 0 returns 413
    /// we shrink to <see cref="ChunkedUploadFallbackChunkSize"/> and retry once.
    /// </summary>
    private const int ChunkedUploadInitialChunkSize = 80 * 1024 * 1024;

    /// <summary>
    /// Fallback chunk size used after a chunk-0 413 from the storage backend. 20 MiB
    /// sits comfortably under the IIS default <c>maxAllowedContentLength</c> of
    /// ~28.6 MiB (FlashBit's storage tier is Microsoft-IIS/10.0, observed 2026-06-03).
    /// If 20 MiB also gets 413 we give up on chunked and fall back to classic — a third
    /// tier of guesses would just delay the inevitable.
    /// </summary>
    private const int ChunkedUploadFallbackChunkSize = 20 * 1024 * 1024;

    /// <summary>
    /// Upload router: dispatches to the chunked or classic protocol based on the
    /// subclass's <see cref="UsesChunkedUpload"/> declaration. No probe-then-fallback —
    /// the declaration is the single source of truth, so misdeclarations fail fast
    /// (visible AttemptFailed with the server's actual response) and the user pays
    /// zero wasted bytes for a probe that just confirms what we already know.
    /// </summary>
    /// <remarks>
    /// On chunked success the api.cgi XML response is normalised into the classic
    /// <c>[{file_code, file_status:"OK"}]</c> JSON shape so the existing
    /// <see cref="ParseUploadResponse"/> works unchanged for both code paths.
    /// </remarks>
    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        // Test override path stays on the classic shape (it's how the existing tests are
        // wired). Only the production path goes through the router.
        if (_uploadOverride is not null)
        {
            return await _uploadOverride(
                ctx.FilePath,
                uploadUrl,
                BuildClassicExtraFields(sessId),
                BrowserClassicHeaders(),
                ctx.SpeedLimitProvider);
        }

        if (UsesChunkedUpload)
        {
            // Subclass declared chunked but the hoster's up.cgi rejected the probe.
            // No fallback — fail loudly so the misdeclaration gets fixed at its source
            // (override UsesChunkedUpload to false) instead of silently masking the
            // real protocol with classic.
            HttpResponseSnapshot? chunkedResult = await TryChunkedUploadAsync(ctx, uploadUrl, sessId) ?? throw new InvalidOperationException(
                $"{Name}: declared UsesChunkedUpload=true but up.cgi did not accept chunk 0. "
                + $"Either the hoster removed chunked support (override UsesChunkedUpload to false) "
                + $"or the API-supplied upload URL ({uploadUrl}) isn't a chunked endpoint.");
            return chunkedResult;
        }

        return await ClassicUploadAsync(ctx, uploadUrl, sessId);
    }

    /// <summary>
    /// Classic XFileSharing upload — one giant <c>multipart/form-data</c> POST to the URL
    /// the API handed us. Browser-shaped per <c>brupload-multipart-quirks</c>.
    /// </summary>
    private Task<HttpResponseSnapshot> ClassicUploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
        => ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "file_0",
            extraFields: BuildClassicExtraFields(sessId),
            headers: BrowserClassicHeaders(),
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);

    /// <summary>
    /// Field set the browser posts alongside the file part for a classic logged-in upload.
    /// <c>protected virtual</c> so web-form hosters whose live capture shows a different set can
    /// override it (isra.cloud sends an empty <c>file_public</c> and no <c>upload</c> button) —
    /// the XFileSharing multipart parser is field-presence/value sensitive (see
    /// <c>brupload-multipart-quirks</c>), so each hoster replicates its own proven set rather than
    /// risk a wasted upload on a near-miss.
    /// </summary>
    protected virtual Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["upload"] = "Start upload",
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Modern XFileSharing chunked upload (verified against hxfile.co's
    /// <c>upload-chunked.js</c> + Fiddler trace on 2026-06-01):
    /// </summary>
    /// <returns>
    /// On chunked success, a synthesised <see cref="HttpResponseSnapshot"/> whose body is
    /// the classic JSON shape so the caller can <see cref="ParseUploadResponse"/> it.
    /// <c>null</c> means the hoster doesn't support the chunked endpoint and the caller
    /// should fall back to classic. Any other failure throws.
    /// </returns>
    private async Task<HttpResponseSnapshot?> TryChunkedUploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        if (!TryDeriveChunkedEndpoints(uploadUrl, out string upCgiUrl, out string apiCgiUrl))
        {
            // URL doesn't end with "upload.cgi" — classic path can't be derived from it
            // either, but we have no chunked endpoint to try. Surface as fallback so
            // ClassicUploadAsync at least tries the URL verbatim.
            return null;
        }

        string clientSid = GenerateChunkSessionId();
        string fileName = Path.GetFileName(ctx.FilePath);
        Dictionary<string, string> headers = BrowserChunkedHeaders();
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileSize = file.Length;
        long position = 0;
        int chunkIndex = 0;
        int currentChunkSize = ChunkedUploadInitialChunkSize;
        bool shrinkAttempted = false;

        while (position < fileSize)
        {
            long thisChunkLen = Math.Min(currentChunkSize, fileSize - position);
            ChunkSliceStream slice = new(file, thisChunkLen);

            HttpResponseSnapshot chunkResp;
            try
            {
                chunkResp = await ctx.Handler.PostChunkAsync(
                    endpoint: upCgiUrl,
                    sid: clientSid,
                    chunkData: slice,
                    chunkLength: thisChunkLen,
                    chunkIndex: chunkIndex,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    headers: headers,
                    getBytesPerSecond: ctx.SpeedLimitProvider,
                    cancellationToken: ctx.Cancellation);
            }
            catch when (chunkIndex == 0)
            {
                // First-chunk transport failure (DNS, refused, TLS) → tentatively chunked-
                // not-supported. Caller falls back to classic which hits the URL verbatim.
                return null;
            }

            // Chunk-0 413 → endpoint exists but rejects our chunk size. Probe-and-shrink:
            // retry chunk 0 once at the smaller fallback size. Storage backends with
            // tight IIS defaults (FlashBit: Microsoft-IIS/10.0, ~28.6 MiB cap, observed
            // 2026-06-03) accept the 20 MiB fallback while still letting hxfile-style
            // CDN frontends use the full 80 MiB on the first try. Rewind the file stream
            // and rotate the sid so any server-side state from the rejected attempt
            // doesn't poison the retry.
            if (chunkIndex == 0 && chunkResp.StatusCode == 413 && !shrinkAttempted)
            {
                ctx.Logger.Log(
                    this,
                    LogType.Status,
                    $"{Name}: chunked up.cgi rejected the {currentChunkSize / (1024 * 1024)} MiB "
                    + $"first chunk with HTTP 413 — retrying at {ChunkedUploadFallbackChunkSize / (1024 * 1024)} MiB.");
                shrinkAttempted = true;
                currentChunkSize = ChunkedUploadFallbackChunkSize;
                file.Position = 0;
                clientSid = GenerateChunkSessionId();
                continue;
            }

            // Chunk-0 fallback gate: ANY non-2xx response on the first chunk drops to
            // classic (or, after a shrink attempt, a second 413 also drops here).
            // Reasons we've actually observed in the wild:
            //   • 404 / 410 / 405 — up.cgi doesn't exist on the storage backend.
            //   • 413 (after shrink) — even the fallback chunk size is too big; give up
            //     on chunked and let the classic path try the original URL.
            //   • Other 4xx (411, 400) — endpoint disagrees with our request shape;
            //     falling back is cheaper than throwing.
            // Later-chunk failures still throw — retrying classic against a partially
            // populated server-side sid would waste the bytes already uploaded.
            if (chunkIndex == 0 && chunkResp.StatusCode is < 200 or >= 300)
            {
                ctx.Logger.Log(
                    this,
                    LogType.Status,
                    $"{Name}: chunked up.cgi rejected chunk 0 with HTTP {chunkResp.StatusCode} "
                    + $"({ChunkSnippet(chunkResp.Body)}) — falling back to classic single-multipart upload.");
                return null;
            }

            if (chunkResp.StatusCode is < 200 or >= 300)
            {
                throw new InvalidOperationException(
                    $"chunked upload: chunk {chunkIndex} returned HTTP {chunkResp.StatusCode} (body: {ChunkSnippet(chunkResp.Body)})");
            }

            if (!ChunkResponseIsOk(chunkResp.Body))
            {
                if (chunkIndex == 0)
                {
                    // Unexpected non-<OK> body on chunk 0 — same diagnosis as a 4xx: this
                    // backend doesn't speak the chunked protocol we expect. Fall back.
                    ctx.Logger.Log(
                        this,
                        LogType.Status,
                        $"{Name}: chunked up.cgi returned unexpected body on chunk 0 "
                        + $"({ChunkSnippet(chunkResp.Body)}) — falling back to classic single-multipart upload.");
                    return null;
                }
                throw new InvalidOperationException(
                    $"chunked upload: chunk {chunkIndex} returned unexpected body: {ChunkSnippet(chunkResp.Body)}");
            }

            position += thisChunkLen;
            chunkIndex++;
        }

        // Finalize.
        Dictionary<string, string> finalizeFields = new(StringComparer.Ordinal)
        {
            ["op"] = "compile",
            ["sid"] = clientSid,
            ["fname"] = fileName,
            ["session_id"] = sessId,
        };
        HttpResponseSnapshot finalizeResp;
        try
        {
            finalizeResp = await PostFormWithHeadersAsync(ctx, apiCgiUrl, finalizeFields, headers);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("chunked upload: api.cgi finalize request failed: " + ex.Message, ex);
        }

        if (finalizeResp.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"chunked upload: api.cgi returned HTTP {finalizeResp.StatusCode} (body: {ChunkSnippet(finalizeResp.Body)})");
        }

        string? fileCode = ParseFinalizeFileCode(finalizeResp.Body);
        if (string.IsNullOrEmpty(fileCode))
        {
            throw new InvalidOperationException(
                $"chunked upload: api.cgi returned 200 but no <Code> in response: {ChunkSnippet(finalizeResp.Body)}");
        }

        // Synthesise the classic-shape JSON so ParseUploadResponse handles both paths.
        string syntheticBody = $"[{{\"file_code\":\"{fileCode}\",\"file_status\":\"OK\"}}]";
        return new HttpResponseSnapshot(200, syntheticBody, finalizeResp.SetCookies);
    }

    /// <summary>
    /// Posts a form-urlencoded body to <paramref name="url"/> with the given browser-
    /// shape headers. Routes through the override when tests have wired one (treats the
    /// finalize call as a "tiny upload" with no file part).
    /// </summary>
    private async Task<HttpResponseSnapshot> PostFormWithHeadersAsync(
        AttemptContext ctx,
        string url,
        IReadOnlyDictionary<string, string> form,
        IReadOnlyDictionary<string, string> headers)
    {
        // PostFormAsync currently doesn't accept extra headers — fold them in via the
        // standard test override slot (form encoded as fields, no file).
        if (_uploadOverride is not null)
        {
            // Override delegate is positional (no parameter names) — pass arguments in
            // order: filePath, endpoint, extraFields, headers, getBytesPerSecond.
            return await _uploadOverride(string.Empty, url, form, headers, null);
        }
        return await ctx.Handler.PostFormAsync(url, form, ctx.Cancellation);
    }

    /// <summary>
    /// Splits the API-returned upload URL into the <c>up.cgi</c> and <c>api.cgi</c>
    /// endpoints used by the chunked protocol. The browser does this by stripping
    /// <c>upload.cgi</c> off the form action and concatenating <c>up.cgi</c> / <c>api.cgi</c>;
    /// we do the same, preserving any query string (some hosters tack
    /// <c>?upload_type=file&amp;utype=reg</c> onto the URL).
    /// </summary>
    internal static bool TryDeriveChunkedEndpoints(string uploadUrl, out string upCgiUrl, out string apiCgiUrl)
    {
        upCgiUrl = string.Empty;
        apiCgiUrl = string.Empty;
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        string path = uri.AbsolutePath;
        const string suffix = "upload.cgi";
        int suffixAt = path.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (suffixAt < 0 || suffixAt + suffix.Length != path.Length)
        {
            return false;
        }
        string basePath = path[..suffixAt];
        UriBuilder upBuilder = new(uri) { Path = basePath + "up.cgi" };
        UriBuilder apiBuilder = new(uri) { Path = basePath + "api.cgi" };
        upCgiUrl = upBuilder.Uri.ToString();
        apiCgiUrl = apiBuilder.Uri.ToString();
        return true;
    }

    /// <summary>
    /// Per-upload session id used as the <c>sid</c> field across all chunks. The browser
    /// generates this client-side as a numeric string; the server treats it opaquely as
    /// long as it's stable within one upload. We use a 12-digit decimal string seeded
    /// from a 48-bit random source — wide enough that two concurrent uploads on the same
    /// account effectively never collide.
    /// </summary>
    private static string GenerateChunkSessionId()
    {
        byte[] buf = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        long n = 0;
        foreach (byte b in buf)
        {
            n = (n << 8) | b;
        }

        n &= 0xFFFFFFFFFFFFL; // 48 bits → up to ~2.8e14, 12-15 decimal digits typically.
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-chunk acknowledgement is the literal string <c>&lt;OK&gt;</c>. Some XFS
    /// deployments wrap it in surrounding whitespace; accept that loosely.
    /// </summary>
    internal static bool ChunkResponseIsOk(string body)
        => body.Trim().StartsWith("<OK>", StringComparison.Ordinal);

    /// <summary>
    /// Pulls the file_code out of the finalize XML. The browser path expects
    /// <c>&lt;Links&gt;&lt;Code&gt;…&lt;/Code&gt;…&lt;/Links&gt;</c>; we also accept the
    /// older XML shape that some deployments use (<c>&lt;root&gt;&lt;Code&gt;…</c>) by
    /// regexing for <c>&lt;Code&gt;</c> directly. Returns null if no code is present —
    /// the caller treats that as a finalize failure.
    /// </summary>
    internal static string? ParseFinalizeFileCode(string xml)
    {
        Match m = _finalizeCodeRegex.Match(xml);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static readonly Regex _finalizeCodeRegex = new(
        @"<Code>\s*([A-Za-z0-9]+)\s*</Code>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ChunkSnippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        string s = body.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private Dictionary<string, string> BuildCookieHeader(string session)
        // A combined cf_clearance-mode session is already a full "name=value; name=value" Cookie
        // header (it contains '='); forward it verbatim. A classic session is a bare xfss token
        // (alphanumeric, never '=') that we wrap. The '=' test cleanly distinguishes the two.
        => new(StringComparer.Ordinal)
        {
            ["Cookie"] = session.Contains('=', StringComparison.Ordinal)
                ? session
                : CookieName + "=" + session,
        };

    private (string? Url, string? Error, bool AuthExpired) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"upload.cgi failed (HTTP {response.StatusCode}): {Snippet(response.Body)}", false);
        }

        UploadResult[]? results;
        try
        {
            results = JsonSerializer.Deserialize<UploadResult[]>(response.Body);
        }
        catch
        {
            results = null;
        }

        if (results is null || results.Length == 0)
        {
            return (null, $"upload.cgi: response was not the expected JSON array: {Snippet(response.Body)}", false);
        }

        UploadResult first = results[0];
        if (string.Equals(first.Status, "Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, true);
        }

        if (!string.Equals(first.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"upload.cgi: file_status={first.Status ?? "(null)"}", false);
        }

        if (string.IsNullOrEmpty(first.Code))
        {
            return (null, "upload.cgi: file_status=OK but file_code was empty", false);
        }

        return (PublicUrlPrefix + first.Code, null, false);
    }

    private static string? ExtractApiKey(string html)
    {
        Match m = _apiKeyRegex.Match(html);
        if (m.Success)
        {
            // One of four groups captures depending on which branch matched (see the regex
            // definition for the four shapes). Pick the non-empty one.
            for (int i = 1; i <= 4; i++)
            {
                if (m.Groups[i].Success && m.Groups[i].Length > 0)
                {
                    return m.Groups[i].Value;
                }
            }
        }

        // Fall back to the bare-token shape (Hxfile): a raw key next to the regenerate link,
        // with no api-url URL to parse. Only reached when none of the four URL shapes matched.
        Match bare = _apiKeyBareTokenRegex.Match(html);
        return bare.Success && bare.Groups[1].Length > 0 ? bare.Groups[1].Value : null;
    }

    private static string? ExtractCsrfToken(string html)
    {
        Match m = _csrfTokenRegex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string captured = m.Groups[1].Success && m.Groups[1].Length > 0
            ? m.Groups[1].Value
            : m.Groups[2].Value;
        return string.IsNullOrEmpty(captured) ? null : captured;
    }

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
    /// Builds the verbose failure detail for <see cref="AccountCheckResult.Detail"/>: the short
    /// human summary followed by the complete, untruncated response body (unlike <see cref="Snippet"/>,
    /// which caps at 200 chars for inline status text). The Add Account "Details" dialog renders
    /// this verbatim, so the body keeps its original line breaks. Falls back to just the summary
    /// when the body is empty.
    /// </summary>
    private static string BuildFailureDetail(string summary, string responseBody)
        => string.IsNullOrWhiteSpace(responseBody)
            ? summary
            : summary + Environment.NewLine + Environment.NewLine + responseBody;

    private async Task<string> GetAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (_getOverride is not null)
        {
            return await _getOverride(url, headers);
        }

        // Production: follow redirects manually. The global HttpHandler runs with
        // AllowAutoRedirect=false (BRupload's login branches on 302), so without this
        // ex-load.com's first /?op=my_account hit (which 302s when the session jar is
        // missing companion cookies like `lang`) lands us on a sub-200-byte stub instead
        // of the logged-in HTML, breaking ApiKey extraction.
        (string body, string _, int _) = await FetchFollowingRedirectsAsync(
            url,
            headers,
            (u, h, t) => ctx.Handler.GetSnapshotAsync(u, h, t),
            ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// CheckAccountAsync's my_account fetch (also reused for the post-generate refetch and
    /// the generate_api_key side-call). When the test override is set we keep the existing
    /// no-redirect semantics so canned-HTML fixtures don't need rewriting; in production
    /// we drive through <see cref="FetchFollowingRedirectsAsync"/> to dodge the
    /// 302-on-first-hit problem ex-load.com exhibits. Returns the final body, the URL we
    /// last hit, and the hop count so the caller can include a useful diagnostic when
    /// extraction fails.
    /// </summary>
    private async Task<(string Body, string FinalUrl, int Hops)> FetchMyAccountAsync(
        HttpHandler handler, string url, IReadOnlyDictionary<string, string> cookieHeader, CancellationToken ct)
    {
        if (_getOverride is not null)
        {
            return (await _getOverride(url, cookieHeader), url, 0);
        }

        return await FetchFollowingRedirectsAsync(
            url,
            cookieHeader,
            (u, h, t) => handler.GetSnapshotAsync(u, h, t),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GETs <paramref name="url"/> and follows 3xx redirects (resolving relative Location
    /// targets against the previous URL), bounded by <paramref name="maxHops"/> — which
    /// is the TOTAL request budget, NOT redirects-after-the-initial. Returns the final
    /// body, the URL we last hit, and the redirect count taken. Static so callers can
    /// stub via the snapshot factory in tests without needing a real HttpHandler. Stops
    /// on the first non-redirect response, on a redirect with no usable Location, or
    /// when the request budget is exhausted (in which case Hops == maxHops and Body is
    /// the last 3xx body for diagnostics).
    /// </summary>
    internal static async Task<(string Body, string FinalUrl, int Hops)> FetchFollowingRedirectsAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseSnapshot>> get,
        CancellationToken ct,
        int maxHops = 5)
    {
        string current = url;
        HttpResponseSnapshot? lastSnap = null;

        // Cookie jar accumulated across redirect hops. ex-load.com's first /?op=my_account
        // hit responds 302 + Set-Cookie: lang=english and redirects back to the SAME URL,
        // expecting the freshly-set cookie on the follow-up (confirmed via browser capture).
        // A plain re-request with the original header alone never sends `lang`, so the
        // server keeps returning a degraded page with no api-url. Seed the jar from the
        // caller's Cookie header, then merge each hop's Set-Cookie — exactly what a browser
        // does — and rebuild the header for the next request.
        Dictionary<string, string> cookieJar = ParseCookieHeader(headers);
        IReadOnlyDictionary<string, string>? currentHeaders = headers;

        for (int attempt = 0; attempt < maxHops; attempt++)
        {
            lastSnap = await get(current, currentHeaders, ct).ConfigureAwait(false);
            bool isRedirect = lastSnap.StatusCode is >= 300 and < 400 && !string.IsNullOrEmpty(lastSnap.LocationHeader);

            // Merge any Set-Cookie values this hop returned into the jar so they ride the
            // next request. Applies on non-redirects too (harmless), but only matters on 3xx.
            if (MergeSetCookies(cookieJar, lastSnap.SetCookies) && headers is not null)
            {
                currentHeaders = RebuildHeadersWithCookies(headers, cookieJar);
            }

            if (!isRedirect)
            {
                // attempt == number of redirects actually followed (0 on a straight 200).
                return (lastSnap.Body, current, attempt);
            }

            // Resolve Location against the current URL so relative paths work
            // (XFS hosters frequently emit "Location: /?op=login" with no scheme).
            current = new Uri(new Uri(current), lastSnap.LocationHeader!).AbsoluteUri;
        }

        // Request budget exhausted — every call within the budget came back 3xx. Return
        // the LAST 3xx body so the caller's diagnostic reflects "we kept getting bounced",
        // and `current` reflects the URL we would have tried next.
        return (lastSnap?.Body ?? string.Empty, current, maxHops);
    }

    /// <summary>Parses a <c>Cookie</c> request header value ("a=1; b=2") into a name→value
    /// map. Returns an empty map when the header dict has no Cookie entry.</summary>
    private static Dictionary<string, string> ParseCookieHeader(IReadOnlyDictionary<string, string>? headers)
    {
        Dictionary<string, string> jar = [with(StringComparer.Ordinal)];
        if (headers is null || !headers.TryGetValue("Cookie", out string? cookie) || string.IsNullOrEmpty(cookie))
        {
            return jar;
        }

        foreach (string pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                jar[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        return jar;
    }

    /// <summary>Merges raw <c>Set-Cookie</c> header values (each "name=value; Path=/; …")
    /// into <paramref name="jar"/>, keeping only the name=value before the first ';'.
    /// Returns true when at least one cookie was added or changed.</summary>
    private static bool MergeSetCookies(Dictionary<string, string> jar, IReadOnlyList<string> setCookies)
    {
        bool changed = false;
        foreach (string raw in setCookies)
        {
            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            string nameValue = (semi < 0 ? raw : raw[..semi]).Trim();
            int eq = nameValue.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = nameValue[..eq];
            string value = nameValue[(eq + 1)..];
            if (!jar.TryGetValue(name, out string? existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                jar[name] = value;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Clones <paramref name="baseHeaders"/> and replaces the <c>Cookie</c> entry
    /// with one serialized from <paramref name="jar"/> ("a=1; b=2"), preserving every
    /// other header the caller set (Origin, etc.).</summary>
    private static Dictionary<string, string> RebuildHeadersWithCookies(IReadOnlyDictionary<string, string> baseHeaders, Dictionary<string, string> jar)
    {
        Dictionary<string, string> rebuilt = new(baseHeaders, StringComparer.Ordinal)
        {
            ["Cookie"] = string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value))
        };
        return rebuilt;
    }

    private async Task<AccountInfo?> TryGetAccountInfoAsync(string apiKey, HttpHandler handler, CancellationToken ct)
    {
        string url = $"{ApiAccountInfoUrl}?key={Uri.EscapeDataString(apiKey)}";
        string body;
        try
        {
            body = _getOverride is not null
                ? await _getOverride(url, null)
                : await handler.GetStringAsync(url, ct);
        }
        catch
        {
            return null;
        }

        try
        {
            AccountInfoResponse? response = JsonSerializer.Deserialize<AccountInfoResponse>(body);
            return response is null || response.Status != 200 ? null : response.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps account/info's premium-expire string into the app's AccountType
    /// taxonomy. Returns Premium when the expiry is in the future, Free otherwise.</summary>
    private static (AccountType Type, DateTime? Expiry) ClassifyPremium(AccountInfo info)
    {
        if (string.IsNullOrEmpty(info.PremiumExpire))
        {
            return (AccountType.Free, null);
        }

        if (!DateTime.TryParse(info.PremiumExpire, System.Globalization.CultureInfo.InvariantCulture, out DateTime expiry))
        {
            return (AccountType.Free, null);
        }

        return expiry > DateTime.UtcNow
            ? (AccountType.Premium, expiry)
            : (AccountType.Free, expiry);
    }

    /// <summary>
    /// Extracts storage usage from the <c>/api/account/info</c> result. Both
    /// <c>storage_used</c> and <c>storage_left</c> arrive as EITHER a JSON string or a JSON
    /// number depending on the hoster — ex-load renders <c>storage_used:"415593052"</c> /
    /// <c>storage_left:"inf"</c> (strings) while KatFile renders
    /// <c>storage_used:"991247477"</c> / <c>storage_left:2198032008075</c> (number). The
    /// fields are typed <see cref="JsonElement"/> so deserialization tolerates both shapes.
    /// Returns (used, quota) where quota = used + left when left is a real number, or null
    /// when left is <c>"inf"</c>/missing/unparseable (the grid's Available cell then renders
    /// "Unlimited"). Used is null only when its field is absent or unparseable.
    /// </summary>
    private static (long? Used, long? Quota) ParseStorageFromAccountInfo(AccountInfo info)
    {
        long? used = TryReadStorageLong(info.StorageUsed);

        // Hexload reports an EMPTY account's storage_used as JSON null (its own dashboard shows
        // "0.00 GB") rather than "0". System.Text.Json maps a JSON null into a JsonElement?
        // property as C# null — indistinguishable from the field being absent — so use
        // storage_left's PRESENCE as the signal that the response carried storage info at all:
        // when storage_left is present but storage_used didn't parse, the account is simply
        // empty → 0 used. Older XFS hosters that omit BOTH fields leave used null/blank.
        if (used is null && info.StorageLeft is not null)
        {
            used = 0L;
        }

        long? quota = null;
        if (used is long usedBytes && TryReadStorageLong(info.StorageLeft) is long left)
        {
            // Real numeric left → cap. "inf" / non-numeric / absent → unlimited (quota null).
            quota = usedBytes + left;
        }

        return (used, quota);
    }

    /// <summary>Reads a byte count out of a storage field that may be a JSON string
    /// (e.g. <c>"991247477"</c>) or a JSON number (e.g. <c>2198032008075</c>). Returns null
    /// for absent fields, the literal <c>"inf"</c>, or anything non-numeric.</summary>
    private static long? TryReadStorageLong(JsonElement? element)
    {
        if (element is not JsonElement e)
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out long n) => n,
            JsonValueKind.String when long.TryParse(e.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long s) => s,
            _ => null,
        };
    }

    private async Task PersistApiKeyAsync(FileHosterLoginDto credentials, string apiKey, CancellationToken ct)
    {
        credentials.ApiKey = apiKey;
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

    private async Task ClearApiKeyAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.ApiKey = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

    // ---- JSON wire types ----

    private sealed class AccountInfoResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("result")] public AccountInfo? Result { get; set; }
    }

    private sealed class AccountInfo
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("premium_expire")] public string? PremiumExpire { get; set; }
        [JsonPropertyName("balance")] public string? Balance { get; set; }

        /// <summary>Bytes currently consumed. Arrives as a JSON string (ex-load,
        /// "415593052") OR a JSON number depending on the hoster — typed
        /// <see cref="JsonElement"/> so deserialization accepts either. Parsed via
        /// <c>TryReadStorageLong</c>.</summary>
        [JsonPropertyName("storage_used")] public JsonElement? StorageUsed { get; set; }

        /// <summary>Remaining storage. A byte count rendered as EITHER a JSON string or a
        /// JSON number (KatFile: <c>2198032008075</c>), or the literal string <c>"inf"</c>
        /// for unlimited (ex-load). Typed <see cref="JsonElement"/> so deserialization
        /// tolerates all three; "inf"/non-numeric → quota null → grid shows "Unlimited".</summary>
        [JsonPropertyName("storage_left")] public JsonElement? StorageLeft { get; set; }
    }

    private sealed class UploadServerResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("sess_id")] public string? SessId { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
    }

    private sealed class UploadResult
    {
        [JsonPropertyName("file_code")] public string? Code { get; set; }
        [JsonPropertyName("file_status")] public string? Status { get; set; }
    }

    [GeneratedRegex("""name=["']token["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']token["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
