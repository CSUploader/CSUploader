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
public abstract class XFileSharingApiPipeline : IFileHosterPipeline
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

    /// <summary>Maximum file size — defaults to the standard 1 GiB free-tier cap. Override
    /// for hosters with different free-tier limits.</summary>
    public virtual long? MaxFileSize => 1L * 1024 * 1024 * 1024;

    /// <summary>Files per upload session — defaults to the standard XFileSharing 30.</summary>
    public virtual int? MaxFilesPerPackage => 30;

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

    /// <summary>
    /// Cookie lifetime applied during the U/P bootstrap window. XFileSharing rarely
    /// returns a real <c>Max-Age</c>; seven days matches the standard "remember me"
    /// horizon on the server side. Once bootstrap completes we throw the cookie away
    /// anyway, so this only matters when a user signs in via U/P but cancels the
    /// my_account scrape — the next attempt can re-use the cookie within this window.
    /// </summary>
    private static readonly TimeSpan DefaultCookieLifetime = TimeSpan.FromDays(7);

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
    private static readonly Regex _csrfTokenRegex = new(
        """name=["']token["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']token["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The API key is rendered in one of three shapes across the XFileSharing family —
    // we accept all three:
    //   1. <input ... name="api-url" value="https://HOST/api/account/info?key=KEY">  (Ex-Load)
    //   2. <input value="...?key=KEY" ... name="api-url" ...>                         (reversed attr order)
    //   3. <span name="api-url">https://HOST/api/account/info?key=KEY</span>          (KatFile — key in text content, not an attribute)
    // The third branch is the trickiest: anchor on `name="api-url"` followed by the
    // closing `>` of the element, then read up to the next `<` as the text node, and
    // pluck `?key=...` out of it. The character class for the key intentionally
    // excludes whitespace, &, ", ', <, and # so we stop at the first delimiter the
    // server would have escaped anyway.
    private static readonly Regex _apiKeyRegex = new(
        """name=["']api-url["'][^>]*?value=["'][^"']*[?&]key=([^"'&]+)["']""" +
        """|value=["'][^"']*[?&]key=([^"'&]+)["'][^>]*?name=["']api-url["']""" +
        """|name=["']api-url["'][^>]*>[^<]*?[?&]key=([^"'&<\s#]+)""",
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
        if (MaxFileSize is long maxBytes && ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds {Name}'s {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()})",
                null);
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

        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<Lib.OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
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
            (string? Url, string? Error, bool AuthExpired) parsed = ParseUploadResponse(uploadResponse);
            if (parsed.AuthExpired)
            {
                await ClearApiKeyAsync(ctx.Credentials, ct).ConfigureAwait(false);
                authExpiredDuringUpload = true;
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

        InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, LoginPagePath);
        string? captured;
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

        if (string.IsNullOrEmpty(captured))
        {
            return null;
        }

        ctx.Credentials.SessionCookie = captured;
        ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + DefaultCookieLifetime;
        ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

        if (_loginRepository is not null)
        {
            try { await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        return captured;
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = password; // XFileSharing API-mode doesn't validate the password — sign-in goes through the WebView captcha.

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

            return new AccountCheckResult(
                IsValid: true,
                AccountType: accountType,
                Message: message,
                PremiumExpiry: expiry,
                ApiKey: apiKey,
                // Surface the email so Settings VM can fill an empty Username column on
                // API-key-direct accounts (the user pasted a key with no email; the grid
                // would otherwise show a blank cell).
                DerivedUsername: info.Email);
        }

        // U/P mode — bootstrap an API key via WebView + my_account scrape.
        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in service unavailable. Restart the app and try again.");
        }

        string? xfss;
        try
        {
            InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, LoginPagePath);
            xfss = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (string.IsNullOrEmpty(xfss))
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled.");
        }

        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(xfss);
        string html;
        try
        {
            html = _getOverride is not null
                ? await _getOverride(MyAccountUrl, cookieHeader)
                : await handler.GetStringAsync(MyAccountUrl, cookieHeader, ct);
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
                return new AccountCheckResult(false, AccountType.Free,
                    "my_account did not contain an API key OR a CSRF token. The sign-in may not have worked. " + Snippet(html));
            }

            string generateUrl = $"{MyAccountUrl}&generate_api_key=1&token={Uri.EscapeDataString(csrf)}";
            try
            {
                _ = _getOverride is not null
                    ? await _getOverride(generateUrl, cookieHeader)
                    : await handler.GetStringAsync(generateUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return new AccountCheckResult(false, AccountType.Free, "generate_api_key request failed: " + ex.Message);
            }

            try
            {
                html = _getOverride is not null
                    ? await _getOverride(MyAccountUrl, cookieHeader)
                    : await handler.GetStringAsync(MyAccountUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return new AccountCheckResult(false, AccountType.Free, "my_account re-fetch failed: " + ex.Message);
            }

            derivedKey = ExtractApiKey(html);
            if (derivedKey is null)
            {
                return new AccountCheckResult(false, AccountType.Free,
                    "my_account did not contain an api-url input after generate. " + Snippet(html));
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

        return new AccountCheckResult(
            IsValid: true,
            AccountType: derivedType,
            Message: derivedMessage,
            PremiumExpiry: derivedInfo is null ? null : ClassifyPremium(derivedInfo).Expiry,
            ApiKey: derivedKey,
            DerivedUsername: derivedInfo?.Email);
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
    /// Normalises the upload-server URL the API hands us. Specifically: when the API
    /// returns <c>https://fsNN.HOST/…</c> for a host that <i>differs</i> from our API
    /// host, downgrade the scheme to <c>http</c>.
    /// </summary>
    /// <remarks>
    /// XFileSharingPro hosters routinely serve their per-user storage subdomains on
    /// shared infrastructure that listens on :443 but with a junk certificate. Observed
    /// in the wild on FlashBit's <c>fs1.flashbit.cc</c>, where :443 presents a
    /// self-signed cert issued for <c>srv1.pusula.co</c> — TLS handshake fails before
    /// the first byte of the upload body is written. The same subdomain answers HTTP
    /// /1.1 on :80 cleanly, and the API key (sess_id, in the request body) is the only
    /// credential in play — there's no cookie or auth header riding the transport that
    /// TLS would protect. Ex-Load already returns <c>http://fs40.ex-load.com/…</c>
    /// directly, and that's the spec-correct shape; FlashBit just returns the wrong
    /// scheme for its own storage.
    /// <para>
    /// We only downgrade when the upload host differs from the API host (Host property).
    /// A URL pointing back at the API host stays HTTPS — the proven-good cert is on the
    /// apex; if a hoster ever exposes upload.cgi at the apex (rare), we want to use it
    /// as-given.
    /// </para>
    /// </remarks>
    private string NormaliseUploadUrlScheme(string uploadUrl)
    {
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

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        // Byte-shape per brupload-multipart-quirks: same XFileSharing upload.cgi backend
        // whether we got here via the API or via the web form. Origin + Sec-Fetch-* are
        // kept for symmetry even though the API path may not strictly need them.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
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

        Dictionary<string, string> uploadHeaders = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Sec-Fetch-Site"] = "same-site",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Dest"] = "empty",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, uploadUrl, extraFields, uploadHeaders, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "file_0",
            extraFields: extraFields,
            headers: uploadHeaders,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    private Dictionary<string, string> BuildCookieHeader(string xfss)
        => new(StringComparer.Ordinal) { ["Cookie"] = CookieName + "=" + xfss };

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
        if (!m.Success) return null;
        // One of three groups captures depending on which branch matched (see the regex
        // definition for the three shapes). Pick the non-empty one.
        for (int i = 1; i <= 3; i++)
        {
            if (m.Groups[i].Success && m.Groups[i].Length > 0)
            {
                return m.Groups[i].Value;
            }
        }
        return null;
    }

    private static string? ExtractCsrfToken(string html)
    {
        Match m = _csrfTokenRegex.Match(html);
        if (!m.Success) return null;
        string captured = m.Groups[1].Success && m.Groups[1].Length > 0
            ? m.Groups[1].Value
            : m.Groups[2].Value;
        return string.IsNullOrEmpty(captured) ? null : captured;
    }

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    private Task<string> GetAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null
            ? _getOverride(url, headers)
            : ctx.Handler.GetStringAsync(url, headers, ct);

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

        try
        {
            await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persist.
        }
    }

    private async Task ClearApiKeyAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.ApiKey = null;

        if (_loginRepository is null)
        {
            return;
        }

        try
        {
            await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
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
}
