// <copyright file="MoneyPlatformPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Extensions;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Shared base for the "moneyplatform" sister sites — FileBoom (fboom.me) and Keep2Share
/// (keep2share.cc / k2s.cc) — which run the identical modern <c>/v1/*</c> OAuth2 API on the same
/// backend (the upload <c>params</c> bundle literally carries <c>"project":"moneyplatform"</c>) and
/// store bytes on the same <c>*.filestore.app</c> nodes. The protocol is identical across both; a
/// concrete hoster supplies only its domains, display name, and free-tier per-file cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth.</b> The OAuth2 password grant on <c>api.&lt;host&gt;/v1/auth/token</c> is captcha-gated
/// and embeds a <c>csrfToken</c> the SPA derives from an existing session, so direct REST login is
/// fragile. Instead the user signs in via a WebView2 modal (<see cref="IInteractiveAuthService"/>) —
/// captcha solved natively inside the embedded browser — and we capture the resulting
/// <c>accessToken</c> JWT cookie (HttpOnly, but <c>CoreWebView2.CookieManager</c> returns it
/// regardless). The API authenticates by that cookie (no Authorization header).
/// </para>
/// <para>
/// <b>Token disambiguation.</b> The login page issues a client-scoped JWT (<c>aud:"client"</c>) on
/// first load and re-issues it user-scoped (<c>aud:"user"</c>) after password+captcha validation.
/// The WebView's cookie-presence signal would fire on the client-scoped token, so we hand a
/// JWT-payload validator to <see cref="InteractiveAuthSpec.CookieValueValidator"/> that only accepts
/// <c>aud:"user"</c>.
/// </para>
/// <para>
/// <b>Upload (three steps, all from our HttpClient after sign-in):</b>
/// </para>
/// <list type="number">
/// <item><description><b>Discovery</b> — <c>GET /v1/files/upload-url</c> with
/// <c>Cookie: accessToken=&lt;jwt&gt;[; pcId=&lt;val&gt;]</c> → <c>{endpoint:{url, params}, signature}</c>;
/// <c>params</c>/<c>signature</c> echoed verbatim into the upload body.</description></item>
/// <item><description><b>Bytes</b> — one multipart POST to <c>endpoint.url</c>
/// (<c>https://prx-NN.filestore.app/upload</c>): seven fields in order — <c>signature</c>,
/// <c>params</c>, <c>ajax="true"</c>, <c>qquuid</c> (we generate), <c>qqfilename</c>,
/// <c>qqtotalfilesize</c>, <c>file</c>; no Cookie/Authorization (auth is in the signed bundle).</description></item>
/// <item><description><b>Parse</b> — <c>{status:"success", success:true, user_file_id, link}</c>;
/// <c>link</c> is the share URL, used verbatim.</description></item>
/// </list>
/// <para>
/// <b>Auth expiry.</b> The accessToken JWT carries a 7-day <c>exp</c>. On HTTP 401 from
/// <c>/v1/files/upload-url</c> we evict the cached state, clear the persisted cookie, and fail with a
/// clear "session expired" message (the next attempt re-runs the WebView).
/// </para>
/// </remarks>
public abstract class MoneyPlatformPipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    /// <summary>Origin of the <c>api.*</c> host plus the <c>/v1</c> prefix, e.g.
    /// <c>https://api.fboom.me/v1</c>. Must not end with a slash.</summary>
    protected abstract string ApiBase { get; }

    /// <summary>The SPA login page the WebView opens (e.g. <c>https://fboom.me/auth/login</c>).</summary>
    protected abstract string LoginUrl { get; }

    /// <summary>Cookie domain the <c>accessToken</c> JWT is set against (e.g. <c>.fboom.me</c>).</summary>
    protected abstract string CookieDomain { get; }

    /// <summary>Site origin sent as <c>Origin</c>/<c>Referer</c> on API + upload requests
    /// (e.g. <c>https://fboom.me</c>). Must not end with a slash; the Referer appends one.</summary>
    protected abstract string SiteOrigin { get; }

    private const string AccessTokenCookieName = "accessToken";
    private const string PcIdCookieName = "pcId";

    private readonly ConcurrentDictionary<int, MoneyPlatformAuthState> _authByCredentialsId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    /// <summary>
    /// Backoff (ms) between retries of the <c>/v1/files/upload-url</c> discovery GET when the API
    /// returns a transient 5xx (observed: HTTP 500 empty body, Cloudflare 524 origin-timeout). One
    /// entry per retry — the array length is the number of retries AFTER the initial attempt. The
    /// discovery GET is idempotent and doesn't touch the file bytes, so retrying is cheap and avoids
    /// failing a whole upload on a momentary server hiccup. Test-overridable so retry tests don't sleep.
    /// </summary>
    internal IReadOnlyList<int> DiscoveryRetryBackoffMs { get; set; } = [2000, 5000];

    /// <summary>Production ctor — WebView2 sign-in via <paramref name="authService"/>, persisted
    /// credentials via <paramref name="loginRepository"/>.</summary>
    protected MoneyPlatformPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor — substitutes the discovery GET and the multipart upload with canned
    /// responders.</summary>
    protected MoneyPlatformPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _authService = authService;
        _loginRepository = loginRepository;
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
    }

    /// <summary>Thrown internally when /v1/files/upload-url returns 401 — the cached JWT expired
    /// (or was revoked) and the caller needs to re-sign-in.</summary>
    private sealed class AuthExpiredException : Exception { }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <summary>
    /// Free downloads are captcha-gated platform-wide, so this lives on the base rather than per
    /// host: the operator's own API docs (keep2share.github.io/api, covering k2s.cc, fboom.me and
    /// tezfiles.com alike) make a guest getUrl demand requestCaptcha/captcha_response and answer
    /// 406 "Captcha required" without it (checked 2026-08-20). Premium tokens skip the step.
    /// </summary>
    public virtual DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <inheritdoc/>
    public abstract long? MaxFileSize { get; }

    public int? MaxFilesPerPackage => null;

    /// <summary>Origin headers the upload host validates. NO Cookie, NO Authorization — the
    /// signature/params bundle carries the auth.</summary>
    private IReadOnlyDictionary<string, string> UploadOriginHeaders => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Origin"] = SiteOrigin,
        ["Referer"] = SiteOrigin + "/",
        ["X-Requested-With"] = "XMLHttpRequest",
    };

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        MoneyPlatformAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out MoneyPlatformAuthState? cached))
        {
            auth = cached;
        }
        else
        {
            (MoneyPlatformAuthState? gated, bool didSignIn, string? error) = await EnsureAuthAsync(ctx, ct);

            if (didSignIn)
            {
                yield return new AuthStarted();
            }

            if (gated is null)
            {
                if (didSignIn)
                {
                    yield return new AuthFailed(error ?? "WebView2 sign-in returned no token");
                }
                yield return new AttemptFailed(error ?? "sign-in failed", null);
                yield break;
            }

            if (didSignIn)
            {
                yield return new AuthSucceeded();
            }

            auth = gated;
        }

        // === Post-auth flow (discovery → multipart upload → parse link) ===
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        do
        {
            // === Pre-flight storage capacity check ===
            // The API rejects /v1/files/upload-url with 403 "Storage limit exceeded" once the
            // account is full. Check available space up front (one cheap stat GET per file) so we
            // fail with a clear, actionable message and never push bytes that can't fit — and so a
            // full account doesn't masquerade as an expired token.
            string? capacityError = await CheckStorageCapacityAsync(ctx, auth);
            if (capacityError is not null)
            {
                attemptFailure = capacityError;
                break;
            }

            // === Discovery: GET /v1/files/upload-url → {endpoint:{url,params}, signature} ===
            UploadEndpoint endpoint;
            string? discoveryError;
            try
            {
                (UploadEndpoint? e, string? err) = await GetUploadEndpointAsync(ctx, auth);
                endpoint = e!;
                discoveryError = err;
            }
            catch (AuthExpiredException)
            {
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                ClearPersistedAuth(ctx.Credentials);
                authExpired = true;
                break;
            }

            if (endpoint is null)
            {
                attemptFailure = discoveryError ?? "/v1/files/upload-url returned no endpoint";
                break;
            }

            yield return new TransferStarted(ctx.FileSize);

            // === Multipart upload ===
            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = UploadBytesAsync(ctx, endpoint);
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

            HttpResponseSnapshot uploadResult;
            try
            {
                uploadResult = await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                attemptCancelled = true;
                break;
            }
            catch (Exception ex)
            {
                attemptException = ex;
                break;
            }

            (string? link, string? uploadError) = ParseUploadResponse(uploadResult);
            if (link is null)
            {
                attemptFailure = uploadError ?? "upload returned no link";
                break;
            }

            finalUrl = link;
        }
        while (false);

        if (authExpired)
        {
            yield return new AuthFailed("accessToken JWT expired");
            yield return new AttemptFailed("accessToken expired — retry to re-sign-in via WebView", null);
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

    private async Task<(MoneyPlatformAuthState? Auth, bool DidSignIn, string? Error)> EnsureAuthAsync(AttemptContext ctx, CancellationToken ct)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out MoneyPlatformAuthState? cached))
            {
                return (cached, false, null);
            }

            // Reuse a persisted (cookie, pcId) bundle when present, the JWT hasn't expired, and the
            // proxy still matches what was pinned at sign-in time.
            if (TryRehydrateFromCredentials(ctx, out MoneyPlatformAuthState? rehydrated))
            {
                _authByCredentialsId[ctx.Credentials.Id] = rehydrated!;
                return (rehydrated, false, null);
            }

            // No usable cookie → drive the WebView. Requires _authService — without it we fail fast
            // (the legacy U/P REST login is not viable; captcha-gated, and the SPA has moved to OAuth2).
            if (_authService is null)
            {
                return (null, false, $"{Name} requires interactive sign-in but no IInteractiveAuthService is registered");
            }

            InteractiveAuthSpec spec = BuildSpec();
            InteractiveAuthResult? captured;
            try
            {
                captured = await _authService.AcquireSessionCookieAsync(
                    spec,
                    ctx.Credentials.Username ?? string.Empty,
                    ctx.Proxy,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (null, true, $"WebView2 sign-in failed: {ex.Message}");
            }

            if (captured is not { } result)
            {
                return (null, true, "WebView2 sign-in cancelled or proxy unsupported");
            }

            MoneyPlatformAuthState fresh = new(result.SessionCookieValue, result.AdditionalCookies);

            // Persist to credentials so subsequent launches (and other files in the same package)
            // skip the WebView. JWT exp drives SessionCookieExpiresUtc.
            DateTime? exp = TryGetJwtExpiry(result.SessionCookieValue);
            ctx.Credentials.SessionCookie = result.SessionCookieValue;
            ctx.Credentials.SessionCookieExpiresUtc = exp ?? DateTime.UtcNow + TimeSpan.FromDays(6);
            ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;
            // Stash pcId on ApiKey — semantic abuse, but ApiKey is the only secondary string column
            // on FileHosterLoginDto and we need somewhere persistent.
            ctx.Credentials.ApiKey = result.AdditionalCookies is { } addl && addl.TryGetValue(PcIdCookieName, out string? pcId)
                ? pcId
                : null;

            // Fetch storage stats opportunistically. Failure here is non-fatal — the upload doesn't
            // need quota info; we use it only for the wizard's queue-time filter and the grid status.
            (long? used, long? total) = await TryFetchStorageStatsAsync(fresh, ctx.Handler, ct).ConfigureAwait(false);
            if (used is not null)
            {
                ctx.Credentials.StorageUsedBytes = used;
            }

            if (total is not null)
            {
                ctx.Credentials.StorageQuotaBytes = total;
            }

            if (_loginRepository is not null)
            {
                await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
            }

            _authByCredentialsId[ctx.Credentials.Id] = fresh;
            return (fresh, true, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryRehydrateFromCredentials(AttemptContext ctx, out MoneyPlatformAuthState? auth)
    {
        auth = null;

        if (string.IsNullOrEmpty(ctx.Credentials.SessionCookie))
        {
            return false;
        }

        // Expiry check: respect the persisted SessionCookieExpiresUtc when set, otherwise fall back
        // to decoding the JWT exp claim ourselves.
        DateTime? expiry = ctx.Credentials.SessionCookieExpiresUtc ?? TryGetJwtExpiry(ctx.Credentials.SessionCookie);
        if (expiry is { } e && e <= DateTime.UtcNow)
        {
            return false;
        }

        // Proxy pin: the JWT may be IP-bound (captcha pins to the IP that solved it). Mismatch ⇒
        // refuse the cached token; treat as if absent.
        if (ctx.Credentials.PinnedProxyId is int pinned && pinned != ctx.Proxy.Id)
        {
            return false;
        }

        Dictionary<string, string>? addl = null;
        if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
        {
            addl = new(StringComparer.Ordinal) { [PcIdCookieName] = ctx.Credentials.ApiKey };
        }

        auth = new MoneyPlatformAuthState(ctx.Credentials.SessionCookie, addl);
        return true;
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password; // never accepted from us — captcha-gated. Sign-in is the WebView path only.
        _ = apiKey;

        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, $"{Name} requires interactive sign-in but no IInteractiveAuthService is registered");
        }

        InteractiveAuthSpec spec = BuildSpec();
        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username ?? string.Empty, proxy, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (captured is not { } result)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled");
        }

        // Derive identity + premium info from the JWT payload — we already have the claims locally
        // so a separate /v1/users/me round-trip is avoidable.
        (string? email, bool premium, DateTime? expiry) = DecodeUserClaimsOrDefault(result.SessionCookieValue);
        AccountType type = premium ? AccountType.Premium : AccountType.Free;

        // Fetch storage stats so the Accounts grid's Used/Available columns get populated and the
        // wizard can filter on quota. Failure here is non-fatal — those columns simply render blank.
        MoneyPlatformAuthState auth = new(result.SessionCookieValue, result.AdditionalCookies);
        (long? storageUsed, long? storageTotal) = await TryFetchStorageStatsAsync(auth, handler, ct).ConfigureAwait(false);

        string message = BuildStatusMessage(type, expiry);

        return new AccountCheckResult(
            IsValid: true,
            AccountType: type,
            Message: message,
            PremiumExpiry: expiry,
            SessionCookie: result.SessionCookieValue,
            SessionCookieExpiresUtc: TryGetJwtExpiry(result.SessionCookieValue) ?? DateTime.UtcNow + TimeSpan.FromDays(6),
            PinnedProxyId: proxy.Id,
            ApiKey: result.AdditionalCookies is { } addl && addl.TryGetValue(PcIdCookieName, out string? pcId) ? pcId : null,
            DerivedUsername: email,
            StorageUsedBytes: storageUsed,
            StorageQuotaBytes: storageTotal);
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard's Summary page: rehydrate the stored JWT and
    /// re-read <c>/v1/users/me/statistic</c> — no WebView. Returns null when there's no usable token
    /// (absent or expired) or the read fails, so the caller keeps the snapshot. The JWT can be
    /// IP-pinned, so a refresh routed through a different proxy than the sign-in may legitimately fail.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // the handler already routes through the chosen proxy.

        if (string.IsNullOrEmpty(credentials.SessionCookie))
        {
            return null;
        }

        DateTime? expiry = credentials.SessionCookieExpiresUtc ?? TryGetJwtExpiry(credentials.SessionCookie);
        if (expiry is { } e && e <= DateTime.UtcNow)
        {
            return null;
        }

        Dictionary<string, string>? addl = string.IsNullOrEmpty(credentials.ApiKey)
            ? null
            : new(StringComparer.Ordinal) { [PcIdCookieName] = credentials.ApiKey };
        MoneyPlatformAuthState auth = new(credentials.SessionCookie, addl);

        (long? used, long? total) = await TryFetchStorageStatsAsync(auth, handler, ct);
        return used is null && total is null ? null : new StorageUsage(used, total);
    }

    /// <summary>
    /// Calls <c>GET /v1/users/me/statistic</c> and returns the parsed (used, total) pair in bytes.
    /// Returns (null, null) on any failure — caller treats that as "unknown", not a hard error.
    /// </summary>
    internal async Task<(long? Used, long? Total)> TryFetchStorageStatsAsync(MoneyPlatformAuthState auth, HttpHandler handler, CancellationToken ct)
    {
        string url = $"{ApiBase}/users/me/statistic";
        IReadOnlyDictionary<string, string> headers = BuildAuthHeaders(auth);

        try
        {
            HttpResponseSnapshot snap = _getOverride is not null
                ? await _getOverride(url, headers).ConfigureAwait(false)
                : await handler.GetSnapshotAsync(url, headers, ct).ConfigureAwait(false);

            if (snap.StatusCode is < 200 or >= 300)
            {
                return (null, null);
            }

            if (!JsonHelpers.TryDeserializeObject(snap.Body ?? string.Empty, out StatisticResponse? env) || env?.StorageSpace is not { } space)
            {
                return (null, null);
            }

            return (space.Used, space.Total);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Composes the human-readable status string for the Accounts grid's Status column. Storage usage
    /// has its own Used/Available columns, so the Status text is just operational state.
    /// </summary>
    private static string BuildStatusMessage(AccountType type, DateTime? premiumExpiry)
        => type == AccountType.Premium
            ? (premiumExpiry is { } e ? string.Format(CultureInfo.InvariantCulture, "Premium until {0:yyyy-MM-dd}", e) : "Premium")
            : "Logged in";

    private InteractiveAuthSpec BuildSpec() => new(
        HosterName: Name,
        LoginUrl: LoginUrl,
        CookieDomain: CookieDomain,
        CookieName: AccessTokenCookieName,
        UsernameCookieName: null,
        CookieValueValidator: IsUserScopedAccessToken,
        AdditionalCookieNames: [PcIdCookieName]);

    private async Task<(UploadEndpoint?, string?)> GetUploadEndpointAsync(AttemptContext ctx, MoneyPlatformAuthState auth)
    {
        string url = $"{ApiBase}/files/upload-url";
        IReadOnlyDictionary<string, string> headers = BuildAuthHeaders(auth);

        // Bounded retry on transient failures. The API intermittently returns a 5xx (HTTP 500 empty
        // body, or Cloudflare 524 origin-timeout) for this GET; the request is idempotent and doesn't
        // transfer the file, so retrying a few times with backoff beats failing the whole upload.
        // 401/403 (auth expired) and 4xx (client error) are terminal — no retry.
        int maxRetries = DiscoveryRetryBackoffMs.Count;
        string? lastTransient = null;

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseSnapshot? snap;
            try
            {
                snap = _getOverride is not null
                    ? await _getOverride(url, headers)
                    : await ctx.Handler.GetSnapshotAsync(url, headers, ctx.Cancellation);
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transport-level failure (DNS, TLS, connection reset). Treat as transient.
                snap = null;
                lastTransient = ex.Message;
            }

            if (snap is not null)
            {
                if (snap.StatusCode == 401)
                {
                    throw new AuthExpiredException();
                }

                if (snap.StatusCode == 403)
                {
                    // 403 is ambiguous: an expired/forbidden token OR a business rule like "Storage
                    // limit exceeded". A 403 carrying a JSON message is a terminal upload failure —
                    // re-login won't help and would needlessly pop the WebView (and clear the
                    // still-valid token), so surface the message. A bare 403 with no message is
                    // treated as auth-expired, preserving the re-login safety net.
                    if (TryGetBusinessError(snap.Body, out string? bizMsg))
                    {
                        return (null, $"{Name} rejected the upload: {bizMsg}");
                    }

                    throw new AuthExpiredException();
                }

                if (snap.StatusCode is < 500 or >= 600)
                {
                    // Terminal: a 2xx to parse, or a 4xx client error to surface as-is.
                    return ParseUploadEndpoint(snap);
                }

                // 5xx (includes Cloudflare 52x like 524) — transient, fall through to retry.
                lastTransient = $"HTTP {snap.StatusCode}";
            }

            if (attempt >= maxRetries)
            {
                return (null,
                    $"/v1/files/upload-url failed after {attempt + 1} attempt(s) (last: {lastTransient}). "
                    + $"{Name}'s API is temporarily unavailable — the upload will retry on the next attempt.");
            }

            try
            {
                await Task.Delay(DiscoveryRetryBackoffMs[attempt], ctx.Cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private static (UploadEndpoint?, string?) ParseUploadEndpoint(HttpResponseSnapshot snap)
    {
        if (!JsonHelpers.TryDeserializeObject(snap.Body ?? string.Empty, out UploadUrlResponse? env) || env is null)
        {
            return (null, FormatApiError("/v1/files/upload-url: response was not JSON", null, snap.StatusCode, snap.Body));
        }

        if (env.Endpoint is not { } ep || string.IsNullOrEmpty(ep.Url) || string.IsNullOrEmpty(ep.Params) || string.IsNullOrEmpty(env.Signature))
        {
            return (null, FormatApiError("/v1/files/upload-url returned an incomplete envelope", env.Message, snap.StatusCode, snap.Body));
        }

        return (new UploadEndpoint(ep.Url, ep.Params, env.Signature), null);
    }

    /// <summary>Pulls a human-readable error out of a JSON error body
    /// (<c>{"message":"Storage limit exceeded"}</c>). Returns false for empty/non-JSON bodies or
    /// bodies without a message.</summary>
    private static bool TryGetBusinessError(string? body, out string? message)
    {
        message = null;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        if (JsonHelpers.TryDeserializeObject(body, out UploadUrlResponse? env) && env?.Message is { Length: > 0 } m)
        {
            message = m;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pre-flight capacity check. Fetches current storage usage and returns an error string when this
    /// file won't fit, or null when it fits OR usage is unknown (a failed stat call shouldn't block an
    /// upload that might still succeed — the 403 "Storage limit exceeded" backstop in
    /// <see cref="GetUploadEndpointAsync"/> still applies).
    /// </summary>
    private async Task<string?> CheckStorageCapacityAsync(AttemptContext ctx, MoneyPlatformAuthState auth)
    {
        (long? used, long? total) = await TryFetchStorageStatsAsync(auth, ctx.Handler, ctx.Cancellation).ConfigureAwait(false);
        if (used is not long u || total is not long t)
        {
            return null; // unknown usage → don't block; the 403 backstop covers a full account
        }

        long available = Math.Max(0, t - u);
        if (ctx.FileSize <= available)
        {
            return null; // fits
        }

        static string Fmt(long bytes) => new ByteUnit(bytes, ByteBase.Binary).ToFriendlyString();
        return string.Format(
            CultureInfo.InvariantCulture,
            "Not enough {0} storage for this file ({1}). Only {2} free of {3} ({4} used). Free up space or upgrade your account, then retry.",
            Name, Fmt(ctx.FileSize), Fmt(available), Fmt(t), Fmt(u));
    }

    private Task<HttpResponseSnapshot> UploadBytesAsync(AttemptContext ctx, UploadEndpoint endpoint)
    {
        // Insertion order matters — the server validates field order in the signed bundle.
        // Dictionary<string,string> preserves insertion order in .NET Core+.
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["signature"] = endpoint.Signature,
            ["params"] = endpoint.Params,
            ["ajax"] = "true",
            ["qquuid"] = Guid.NewGuid().ToString("D"),
            ["qqfilename"] = ctx.FileName,
            ["qqtotalfilesize"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
        };

        IReadOnlyDictionary<string, string> headers = UploadOriginHeaders;
        return _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, endpoint.Url, fields, headers, ctx.SpeedLimitProvider)
            : ctx.Handler.UploadMultipartAsync(
                ctx.FilePath,
                endpoint.Url,
                fileFieldName: "file",
                extraFields: fields,
                headers: headers,
                getBytesPerSecond: ctx.SpeedLimitProvider,
                cancellationToken: ctx.Cancellation);
    }

    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot snap)
    {
        if (snap.StatusCode is < 200 or >= 300)
        {
            return (null, FormatApiError("upload failed", null, snap.StatusCode, snap.Body));
        }

        if (!JsonHelpers.TryDeserializeObject(snap.Body ?? string.Empty, out UploadResponse? env) || env is null)
        {
            return (null, FormatApiError("upload: response was not JSON", null, snap.StatusCode, snap.Body));
        }

        if (!string.Equals(env.Status, "success", StringComparison.Ordinal) || string.IsNullOrEmpty(env.Link))
        {
            return (null, FormatApiError("upload failed", env.Message, snap.StatusCode, snap.Body));
        }

        return (env.Link, null);
    }

    private IReadOnlyDictionary<string, string> BuildAuthHeaders(MoneyPlatformAuthState auth)
    {
        System.Text.StringBuilder cookie = new();
        cookie.Append(AccessTokenCookieName).Append('=').Append(auth.AccessToken);
        if (auth.AdditionalCookies is { } addl)
        {
            foreach (KeyValuePair<string, string> kv in addl)
            {
                cookie.Append("; ").Append(kv.Key).Append('=').Append(kv.Value);
            }
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cookie"] = cookie.ToString(),
            ["Origin"] = SiteOrigin,
            ["Referer"] = SiteOrigin + "/",
        };
    }

    private void ClearPersistedAuth(FileHosterLoginDto credentials)
    {
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;
        credentials.ApiKey = null;
        if (_loginRepository is not null)
        {
            // Fire-and-forget — we don't await inside a yield-illegal context. The DTO mutation is
            // what matters for the in-flight retry; the row update can lag.
            _ = _loginRepository.UpdateAsync(credentials, CancellationToken.None);
        }
    }

    /// <summary>True iff <paramref name="jwt"/> decodes as a JWT whose payload has <c>aud:"user"</c>.
    /// The pre-login bootstrap token uses <c>aud:"client"</c> — same cookie name (<c>accessToken</c>)
    /// so this validator is what distinguishes the "signed in" state from the "page loaded" state in
    /// the WebView.</summary>
    internal static bool IsUserScopedAccessToken(string jwt)
    {
        JsonElement? payload = TryDecodeJwtPayload(jwt);
        return payload is { } p
            && p.TryGetProperty("aud", out JsonElement aud)
            && aud.ValueKind == JsonValueKind.String
            && string.Equals(aud.GetString(), "user", StringComparison.Ordinal);
    }

    /// <summary>Reads the JWT's <c>exp</c> claim (unix seconds) and converts to UTC.</summary>
    internal static DateTime? TryGetJwtExpiry(string jwt)
    {
        JsonElement? payload = TryDecodeJwtPayload(jwt);
        if (payload is not { } p)
        {
            return null;
        }

        if (!p.TryGetProperty("exp", out JsonElement exp))
        {
            return null;
        }

        if (!exp.TryGetInt64(out long unix))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    }

    /// <summary>Pulls (email, isPremium, premiumExpiry) from the JWT payload. The <c>name</c> claim
    /// carries the email; <c>role</c> distinguishes "registered" (free) from "premium". Free accounts
    /// have no premium expiry. Falls back to <c>(null, false, null)</c> when the payload doesn't
    /// decode.</summary>
    private static (string? Email, bool IsPremium, DateTime? Expiry) DecodeUserClaimsOrDefault(string jwt)
    {
        JsonElement? payload = TryDecodeJwtPayload(jwt);
        if (payload is not { } p)
        {
            return (null, false, null);
        }

        string? email = p.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

        bool premium = p.TryGetProperty("role", out JsonElement role)
            && role.ValueKind == JsonValueKind.String
            && string.Equals(role.GetString(), "premium", StringComparison.OrdinalIgnoreCase);

        // No premium-expiry claim on the JWT; surfacing one would need a /v1/users/me round-trip.
        return (email, premium, null);
    }

    private static JsonElement? TryDecodeJwtPayload(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            string base64Url = parts[1];
            // base64url → base64: replace - with +, _ with /, then pad to multiple of 4.
            string padded = base64Url.Replace('-', '+').Replace('_', '/');
            int rem = padded.Length % 4;
            if (rem == 2)
            {
                padded += "==";
            }
            else if (rem == 3)
            {
                padded += "=";
            }
            else if (rem == 1)
            {
                return null; // invalid base64url length
            }

            byte[] bytes = Convert.FromBase64String(padded);
            using var doc = JsonDocument.Parse(bytes);
            // Clone before disposing the document so the returned element stays valid.
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatApiError(string fallback, string? details, int? status, string? rawBody = null)
    {
        if (status is int s and not 200)
        {
            string head = details is { Length: > 0 } ? details : fallback;
            return $"{head} (HTTP {s})";
        }

        if (details is { Length: > 0 })
        {
            return details;
        }

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            string snippet = rawBody.Trim()
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            const int Max = 200;
            if (snippet.Length > Max)
            {
                snippet = snippet[..Max] + "…";
            }

            return $"{fallback}: {snippet}";
        }

        return fallback;
    }

    /// <summary>Bundled output of GET /v1/files/upload-url ready to drive the multipart POST.</summary>
    private sealed record UploadEndpoint(string Url, string Params, string Signature);

    private sealed class UploadUrlResponse
    {
        [JsonPropertyName("endpoint")] public UploadUrlEndpoint? Endpoint { get; set; }

        [JsonPropertyName("signature")] public string? Signature { get; set; }

        // Present on error envelopes the API may surface on non-401 failures. Optional.
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class UploadUrlEndpoint
    {
        [JsonPropertyName("url")] public string? Url { get; set; }

        [JsonPropertyName("params")] public string? Params { get; set; }
    }

    private sealed class UploadResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }

        [JsonPropertyName("success")] public bool Success { get; set; }

        [JsonPropertyName("status_code")] public int StatusCode { get; set; }

        [JsonPropertyName("user_file_id")] public string? UserFileId { get; set; }

        [JsonPropertyName("link")] public string? Link { get; set; }

        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    /// <summary>Envelope for <c>GET /v1/users/me/statistic</c>: surfaces storage usage
    /// (<c>storageSpace.{used,total}</c>) and daily traffic counters. We consume only the storage
    /// half — traffic isn't used by the wizard.</summary>
    private sealed class StatisticResponse
    {
        [JsonPropertyName("storageSpace")] public StatisticSpace? StorageSpace { get; set; }

        [JsonPropertyName("dailyTraffic")] public StatisticSpace? DailyTraffic { get; set; }

        [JsonPropertyName("downloadedTotal")] public long DownloadedTotal { get; set; }
    }

    private sealed class StatisticSpace
    {
        [JsonPropertyName("total")] public long Total { get; set; }

        [JsonPropertyName("used")] public long Used { get; set; }
    }
}
