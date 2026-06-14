// <copyright file="ExtMatrixPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// ExtMatrix — <b>DISABLED 2026-06-07</b>. Custom REST API documented at
/// <c>/api/docs.php</c>: single-multipart POST to <c>/api/upload.php</c> with
/// <c>api_key</c> + <c>file</c>, plain-text responses (<c>upload_success</c> /
/// <c>upload_failed</c> / <c>invalid_api</c>). The auth flow + my_account scrape work
/// (verified end-to-end via Fiddler 2026-06-06), but the upload endpoint is unusable.
/// Code retained for the day ExtMatrix fixes their backend; <b>do not re-enable without
/// reading the diagnosis below and re-verifying every item.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why disabled — origin-side body cap below the advertised limit:</b>
/// </para>
/// <list type="number">
///   <item>The docs claim a 250 MiB per-file limit, but a POST to <c>/api/upload.php</c>
///   with a real ~27 MiB file returns <c>413 Payload Too Large</c> from the origin's
///   <c>nginx</c> (Cloudflare passes the request through, then the origin's nginx kills
///   it on body size). So the practical cap on the simple API endpoint is well below
///   what they advertise — almost certainly an <c>nginx client_max_body_size</c>
///   directive in the low-tens-of-MiB range.</item>
///   <item>The web UI achieves the 250 MiB limit via a chunked upload protocol (each
///   chunk fits under nginx's cap, server reassembles). But: the chunked protocol is
///   <b>undocumented</b> — the public <c>/api/docs.php</c> only describes the simple
///   single-POST endpoint.</item>
///   <item>We can't capture the chunked protocol from the web UI either: the live web
///   UI is currently <b>also failing for our test user</b>, so there's no successful
///   trace to reverse-engineer.</item>
/// </list>
/// <para>
/// We DID get the simple <c>/api/upload.php</c> POST past Cloudflare's WAF by adding
/// browser-shape headers (see <see cref="BrowserUploadHeaders"/> — Origin, Referer,
/// Sec-Fetch-*). That fix unblocks the request from getting a TCP RST during the body
/// upload, but doesn't help with nginx's body-size cap, which is a hard backend limit.
/// </para>
/// <para>
/// <b>Re-enable checklist</b> (verify the first item OR the second; then walk steps 3–6):
/// </para>
/// <list type="number">
///   <item>Confirm <c>POST /api/upload.php</c> succeeds for a file ≥30 MiB (any one of
///   our test files would do). If ExtMatrix has raised <c>client_max_body_size</c> on
///   this endpoint, the existing <see cref="UploadAsync"/> path Just Works.</item>
///   <item>OR capture a successful chunked upload from extmatrix.com's web UI and
///   implement the chunked protocol in <see cref="UploadAsync"/> (likely shape: a
///   per-chunk POST endpoint + a finalize call, similar to the XFS chunked pattern
///   we already have in <see cref="XFileSharingApiPipeline.TryChunkedUploadAsync"/>).</item>
///   <item>Uncomment the DI registration in <c>App.xaml.cs</c>.</item>
///   <item>Uncomment the <c>"ExtMatrix"</c> entry in
///   <c>FileHosterClient.FileHosters</c>.</item>
///   <item>Add <c>"ExtMatrix"</c> back to <c>EditAccountWindow.ApiKeyHosters</c>.</item>
///   <item>Flip the smoke test's registry-presence assertion back to
///   <c>Assert.True</c>.</item>
/// </list>
/// <para>
/// <b>Auth model (preserved for re-enable)</b> — two paths land on the same end state
/// (an <see cref="FileHosterLoginDto.ApiKey"/>):
/// </para>
/// <list type="bullet">
///   <item><b>API-key direct</b>: user pastes the key from
///   <c>/members/account.php</c>; verification probes <c>/api/info.php</c> with the key
///   and a sentinel <c>file_id</c>. A response containing <c>invalid_api</c> means the
///   key is bad; anything else means the key is accepted.</item>
///   <item><b>U/P bootstrap</b>: pops <see cref="IInteractiveAuthService"/> for an
///   interactive sign-in (which captures both the <c>auth</c> session cookie and the
///   <c>username</c> identity cookie), GETs <c>/members/account.php</c>, scrapes the
///   API key (generating one via <c>?task=get_api_key</c> when missing), persists onto
///   the DTO.</item>
/// </list>
/// </remarks>
public sealed class ExtMatrixPipeline : IFileHosterPipeline
{
    private const string Host = "https://www.extmatrix.com";

    /// <summary>Login page WebView2 lands on for the interactive sign-in flow.
    /// ExtMatrix uses <c>/login.php</c> (PHP) — NOT the XFS-family <c>/login.html</c>.</summary>
    private const string LoginUrl = Host + "/login.php";

    /// <summary>Session cookie name — confirmed via Fiddler capture 2026-06-06. After a
    /// successful login, the server sets <c>Set-Cookie: auth=&lt;hex&gt;; domain=.extmatrix.com</c>
    /// (alongside a <c>username</c> cookie that's purely informational). The
    /// <c>auth</c> cookie is what authenticates subsequent /members/ requests.</summary>
    private const string CookieName = "auth";

    /// <summary>Identity cookie name — ExtMatrix sets <c>Set-Cookie: username=...</c>
    /// alongside <c>auth</c> on a successful login (confirmed via Fiddler capture
    /// 2026-06-06). Passed to the WebView via <see cref="InteractiveAuthSpec.UsernameCookieName"/>
    /// so the captured identity flows back through <see cref="InteractiveAuthResult.CapturedUsername"/>
    /// and lands on the credentials DTO — this is the single source of truth for the
    /// account's displayed username; no my_account HTML scrape required.</summary>
    private const string UsernameCookieName = "username";

    private const string CookieDomain = ".extmatrix.com";

    private const string MyAccountUrl = Host + "/members/account.php";

    /// <summary>Generate-API-key endpoint. Lives at the apex (<c>/account.php</c>) — NOT
    /// at <c>/members/account.php</c>. The HTML link reads <c>./account.php?task=get_api_key</c>
    /// but the page carries <c>&lt;base href="https://www.extmatrix.com/"&gt;</c>, so it
    /// resolves against the apex rather than the current dir. Confirmed via capture
    /// 2026-06-06 (the browser navigation lands on <c>/account.php?task=get_api_key</c>
    /// and 302s back to <c>/account.php</c>, after which the key is visible on
    /// <see cref="MyAccountUrl"/> too).</summary>
    private const string GenerateApiKeyUrl = Host + "/account.php?task=get_api_key";

    private const string ApiUploadUrl = Host + "/api/upload.php";
    private const string ApiInfoUrl = Host + "/api/info.php";

    /// <summary>Cookie lifetime for the brief U/P-bootstrap window. We throw the cookie
    /// away once an API key is in hand, so this only matters when a user signs in via U/P
    /// but cancels before my_account is scraped — the next attempt can reuse the cookie
    /// within this window. Matches <see cref="XFileSharingApiPipeline"/>'s default.</summary>
    private static readonly TimeSpan DefaultCookieLifetime = TimeSpan.FromDays(7);

    /// <summary>One bootstrap at a time per credentials id — prevents N parallel uploads
    /// on a brand-new account from each popping their own WebView.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _bootstrapGates = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<string>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    /// <summary>API key extractor — four branches:
    /// <list type="number">
    ///   <item><b>Label-anchored</b> (ExtMatrix's actual rendering): the input has no
    ///   <c>name=</c> attribute, just <c>disabled="disabled" value="KEY"</c> immediately
    ///   after a <c>API Key:</c> label cell. Pattern hunts forward from the literal
    ///   text "API Key" to the next <c>&lt;input&gt;</c>'s <c>value=</c>.</item>
    ///   <item><b>XFS-style name-then-value</b>: <c>name="api_key"</c> followed by
    ///   <c>value="KEY"</c>. Retained for forward compatibility — ExtMatrix doesn't
    ///   currently emit this shape, but XFS-derived forks often do.</item>
    ///   <item><b>XFS-style value-then-name</b>: <c>value="KEY"</c> followed by
    ///   <c>name="api_key"</c>.</item>
    ///   <item><b>Query-parameter fallback</b>: <c>?api_key=KEY</c> appears in some
    ///   account pages inside a sample-URL block.</item>
    /// </list>
    /// All branches are case-insensitive; the label-anchored branch uses
    /// <see cref="RegexOptions.Singleline"/> so <c>.</c> matches the newlines between the
    /// label cell and the input cell.</summary>
    private static readonly Regex _apiKeyRegex = new(
        """API\s+Key\s*:[^<]*<[^>]*>(?:[^<]|<(?!input))*?<input[^>]*?\bvalue\s*=\s*["']([^"']+)["']""" +
        """|name=["']api[_-]?key["'][^>]*?value=["']([^"']+)["']""" +
        """|value=["']([^"']+)["'][^>]*?name=["']api[_-]?key["']""" +
        """|[?&]api[_-]?key=([A-Za-z0-9._-]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>URL extractor used to recover the public download URL from the
    /// <c>upload_success</c> body. Permissive on purpose — the docs don't quote the exact
    /// shape; we grab the first absolute http(s) URL up to the next likely delimiter.
    /// The exclusion class blocks whitespace, HTML brackets/quotes, and the common
    /// inline-separator characters <c>|</c> and <c>,</c> some XFS-derived deployments
    /// use between marker and URL — without those, a pipe-separated success body
    /// (<c>upload_success|URL1|URL2</c>) would be matched as one giant glued URL.</summary>
    private static readonly Regex _httpUrlRegex = new(
        @"https?://[^\s""'<>|,]+",
        RegexOptions.Compiled);

    public ExtMatrixPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    internal ExtMatrixPipeline(
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

    /// <inheritdoc/>
    public string Name => "ExtMatrix";

    /// <inheritdoc/>
    public bool RequiresHashingBeforeUpload => false;

    /// <inheritdoc/>
    public bool RequiresHashingAfterUpload => false;

    /// <inheritdoc/>
    public long? MaxFileSize => 250L * 1024 * 1024;

    /// <summary>
    /// No per-package count cap. ExtMatrix's protocol is single-POST-per-file at
    /// <c>/api/upload.php</c> — there's no session ID to share across files, so a
    /// package of N files just means N independent POSTs.
    /// </summary>
    /// <remarks>
    /// The free-tier "1 simultaneous upload" the user mentioned during ExtMatrix's
    /// implementation review is a <i>concurrency</i> constraint (don't run two
    /// ExtMatrix uploads in parallel), not a batch-size constraint. The codebase
    /// doesn't yet have per-hoster concurrency throttling — when it does, ExtMatrix
    /// should pin to 1 in-flight upload. Tracked as a follow-up; the immediate fix
    /// here is to stop the wizard from incorrectly truncating ExtMatrix packages to
    /// a single file.
    /// </remarks>
    public int? MaxFilesPerPackage => null;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        if (MaxFileSize is long maxBytes && ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds {Name}'s {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()})",
                null);
            yield break;
        }

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

        // Upload — single-multipart POST.
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        yield return new TransferStarted(ctx.FileSize);

        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, apiKey);

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
            (string? Url, string? Error, bool Invalid) parsed = ParseUploadResponse(uploadResponse);
            if (parsed.Invalid)
            {
                await ClearApiKeyAsync(ctx.Credentials, ct).ConfigureAwait(false);
                authExpired = true;
            }
            else if (parsed.Error is not null)
            {
                attemptFailure = parsed.Error;
            }
            else
            {
                finalUrl = parsed.Url;
            }
        }

        if (authExpired)
        {
            yield return new AuthFailed("API key rejected by ExtMatrix — re-authenticate from Settings → Accounts");
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

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = password; // ExtMatrix's WebView captures the session cookie; we never see the password.

        // API-key-direct verification path: probe /api/info.php with a sentinel file_id.
        // The docs guarantee `invalid_api` for a bad key; any other response (including a
        // PHP error about missing file_id) means the key reached the auth layer cleanly.
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (await ProbeApiKeyAsync(apiKey, handler, ct))
            {
                return new AccountCheckResult(
                    IsValid: true,
                    AccountType: AccountType.Free,
                    Message: "API key accepted",
                    ApiKey: apiKey);
            }

            return new AccountCheckResult(false, AccountType.Free, "API key was rejected by /api/info.php (got invalid_api).");
        }

        // U/P bootstrap — pop WebView, scrape my_account.
        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in service unavailable. Restart the app and try again.");
        }

        InteractiveAuthResult? captured;
        try
        {
            // UsernameCookieName: "username" — ExtMatrix sets it alongside the auth cookie
            // at login (verified via Fiddler 2026-06-06: Set-Cookie: username=ufcyi43133).
            // It's the canonical identity source; we don't need to scrape the my_account
            // page for it.
            InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, UsernameCookieName);
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (captured is not InteractiveAuthResult auth)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled.");
        }

        (string? derivedKey, string? scrapeError) = await ScrapeApiKeyAsync(handler, auth.SessionCookieValue, ct);
        if (derivedKey is null)
        {
            return new AccountCheckResult(false, AccountType.Free, scrapeError ?? "Could not extract API key from /members/account.php.");
        }


        // DerivedUsername flows back to EditAccountWindow.SignInButton_Click, which stores
        // it on the DTO so the Accounts grid and the read-only UsernameDisplay chip both
        // surface the captured identity.
        return new AccountCheckResult(
            IsValid: true,
            AccountType: AccountType.Free,
            Message: "Signed in",
            ApiKey: derivedKey,
            DerivedUsername: auth.CapturedUsername);
    }

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
        string? sessionCookie = await GetOrAcquireSessionCookieAsync(ctx, ct);
        if (sessionCookie is null)
        {
            return (null, true, "sign-in cancelled or no usable proxy available");
        }

        // Username is already on ctx.Credentials at this point — GetOrAcquireSessionCookieAsync
        // captured it from the WebView's identity cookie and wrote it inline.
        (string? derivedKey, string? scrapeError) = await ScrapeApiKeyAsync(ctx.Handler, sessionCookie, ct);
        if (derivedKey is null)
        {
            return (null, true, scrapeError ?? "could not extract API key from /members/account.php");
        }

        await PersistApiKeyAsync(ctx.Credentials, derivedKey, ct).ConfigureAwait(false);
        ctx.Logger.Log(this, LogType.Status, $"{Name}: bootstrapped API key for {ctx.Credentials.Username}");
        return (derivedKey, true, null);
    }

    private async Task<string?> GetOrAcquireSessionCookieAsync(AttemptContext ctx, CancellationToken ct)
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

        // UsernameCookieName: "username" so the WebView surfaces the identity alongside
        // the session cookie. Persisted inline below so the Accounts grid / EditAccount
        // dialog picks it up on next refresh.
        InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, UsernameCookieName);
        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(
                spec,
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

        ctx.Credentials.SessionCookie = result.SessionCookieValue;
        ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + DefaultCookieLifetime;
        ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;
        if (result.CapturedUsername is not null)
        {
            ctx.Credentials.Username = result.CapturedUsername;
        }

        if (_loginRepository is not null)
        {
            await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
        }

        return result.SessionCookieValue;
    }

    /// <summary>
    /// GETs <c>/members/account.php</c> and extracts the API key. When the page doesn't
    /// yet contain a key (link reads <c>[Get API Key]</c>), POKEs <c>?task=get_api_key</c>
    /// and re-fetches once. Username scraping intentionally removed — the canonical
    /// identity comes from the <c>username</c> cookie captured by the WebView (see
    /// <see cref="UsernameCookieName"/>), so there's no second source to merge.
    /// </summary>
    private async Task<(string? ApiKey, string? Error)> ScrapeApiKeyAsync(HttpHandler handler, string sessionCookie, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(sessionCookie);

        string html;
        try
        {
            html = await GetAsync(handler, MyAccountUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return (null, "my_account fetch failed: " + ex.Message);
        }

        string? key = ExtractApiKey(html);
        if (key is not null)
        {
            return (key, null);
        }

        // No key yet — hit the generate endpoint and re-fetch.
        try
        {
            _ = await GetAsync(handler, GenerateApiKeyUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return (null, "generate_api_key request failed: " + ex.Message);
        }

        try
        {
            html = await GetAsync(handler, MyAccountUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return (null, "my_account re-fetch failed after generate: " + ex.Message);
        }

        key = ExtractApiKey(html);
        return key is not null
            ? (key, null)
            : (null, "my_account did not contain an API key after generate. " + Snippet(html));
    }

    /// <summary>
    /// Browser-shape headers added to every <c>/api/upload.php</c> POST. Without these,
    /// Cloudflare in front of <c>www.extmatrix.com</c> drops the connection mid-stream
    /// with a TCP RST (observed as <c>SocketException 10054</c> during
    /// <c>ProgressStreamContent.SerializeToStreamAsync</c>) — the WAF's bot detection
    /// flags requests with no <c>Origin</c> / <c>Referer</c> / <c>Sec-Fetch-*</c>
    /// signature even when the API itself only requires <c>api_key</c>. Same pattern we
    /// already use on the XFS classic upload path (see
    /// <c>XFileSharingApiPipeline.BrowserClassicHeaders</c>).
    /// </summary>
    private static Dictionary<string, string> BrowserUploadHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Sec-Fetch-Site"] = "same-origin",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
        ["Accept"] = "*/*",
    };

    /// <summary>
    /// Posts the file as a single multipart upload to <c>/api/upload.php</c>. The form
    /// fields are <c>api_key</c> (auth) and <c>file</c> (the actual bytes). When a test
    /// override is wired, takes the override path so we can drive the parsing tests
    /// against canned responses.
    /// </summary>
    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string apiKey)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal) { ["api_key"] = apiKey };
        Dictionary<string, string> headers = BrowserUploadHeaders();

        if (_uploadOverride is not null)
        {
            // Func<> delegate parameters are positional, not named — keep the argument
            // order: filePath, endpoint, extraFields, headers, getBytesPerSecond.
            return await _uploadOverride(
                ctx.FilePath,
                ApiUploadUrl,
                extraFields,
                headers,
                ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            ApiUploadUrl,
            fileFieldName: "file",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    private async Task<bool> ProbeApiKeyAsync(string apiKey, HttpHandler handler, CancellationToken ct)
    {
        // Sentinel file_id=0 — doesn't exist; we expect a "file not found"-style error
        // from a valid key OR `invalid_api` from a bad key. We treat anything that does
        // NOT contain `invalid_api` as a passing probe (i.e. the key reached the auth
        // layer). This avoids creating a stub file just to verify the key.
        string url = $"{ApiInfoUrl}?api_key={Uri.EscapeDataString(apiKey)}&file_id=0";

        string body;
        try
        {
            body = _getOverride is not null
                ? await _getOverride(url, null)
                : await handler.GetStringAsync(url, ct);
        }
        catch
        {
            return false;
        }

        return !body.Contains("invalid_api", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the plain-text upload response. ExtMatrix doesn't document the exact body
    /// shape — only that success contains <c>upload_success</c> followed by a download URL
    /// and a deletion URL, while failures contain <c>upload_failed</c> or <c>invalid_api</c>.
    /// We extract the first absolute <c>http(s)</c> URL from the body and treat it as the
    /// public URL on success.
    /// </summary>
    /// <returns>One of three states: <c>(Url, null, false)</c> on success;
    /// <c>(null, errorMessage, false)</c> on a generic upload failure;
    /// <c>(null, null, true)</c> when the API key was rejected (caller clears the cached
    /// key and forces a re-bootstrap on retry).</returns>
    internal static (string? Url, string? Error, bool Invalid) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"upload.php returned HTTP {response.StatusCode}: {Snippet(response.Body)}", false);
        }

        string body = response.Body ?? string.Empty;

        if (body.Contains("invalid_api", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, true);
        }

        if (body.Contains("upload_failed", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"upload.php returned upload_failed: {Snippet(body)}", false);
        }

        if (!body.Contains("upload_success", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"upload.php returned an unrecognised body: {Snippet(body)}", false);
        }

        Match m = _httpUrlRegex.Match(body);
        if (!m.Success)
        {
            return (null, "upload.php returned upload_success but no public URL was found in the body.", false);
        }

        return (m.Value, null, false);
    }

    internal static string? ExtractApiKey(string html)
    {
        Match m = _apiKeyRegex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        // Iterate the four capture groups in the regex (label-anchored, name-then-value,
        // value-then-name, query-param fallback) and return whichever one matched.
        for (int i = 1; i <= 4; i++)
        {
            if (m.Groups[i].Success && m.Groups[i].Length > 0)
            {
                return m.Groups[i].Value;
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildCookieHeader(string sessionCookie)
        => new(StringComparer.Ordinal) { ["Cookie"] = CookieName + "=" + sessionCookie };

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    private Task<string> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null
            ? _getOverride(url, headers)
            : handler.GetStringAsync(url, headers, ct);

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
}
