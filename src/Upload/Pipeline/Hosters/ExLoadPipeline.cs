// <copyright file="ExLoadPipeline.cs" company="CSUploader">
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
/// Ex-Load upload pipeline. Mechanically identical to <see cref="BRuploadPipeline"/>
/// (same XFileSharing-family backend: <c>?op=upload_form</c> scrape, multipart POST,
/// <c>{file_code, file_status}</c> JSON response), with one critical difference:
/// the login is gated by hCaptcha, so the credential POST path doesn't work. Instead,
/// the pipeline asks <see cref="IInteractiveAuthService"/> to pop a WebView2 window in
/// which the user completes the captcha flow; the resulting <c>xfss</c> cookie is then
/// used and persisted to the DB so the user only re-runs the WebView dance once per
/// cookie lifetime.
/// </summary>
/// <remarks>
/// <para>
/// Per-credentials session caching mirrors BRupload: in-memory <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// keyed by <see cref="FileHosterLoginDto.Id"/>, hydrated on first use from the DB-cached
/// cookie + <c>?op=upload_form</c> scrape, invalidated on auth-expired upload response.
/// </para>
/// <para>
/// Upload-shape parity with BRupload (boundary unquoting, name= quoting, real MIME,
/// Origin + Sec-Fetch-* headers) is handled by the shared
/// <see cref="HttpHandler.UploadMultipartAsync"/> code path — we just supply the
/// hoster-specific extra fields and headers.
/// </para>
/// </remarks>
public sealed class ExLoadPipeline : IFileHosterPipeline
{
    private const string Host = "https://ex-load.com";
    private const string LoginUrl = Host + "/login.html";
    private const string UploadFormUrl = Host + "/?op=upload_form";
    private const string PublicUrlPrefix = Host + "/";
    private const string CookieName = "xfss";
    private const string CookieDomain = ".ex-load.com";
    private const string LoginPagePath = "/login.html";

    /// <summary>
    /// Conservative default cookie lifetime applied at capture. XFileSharing rarely
    /// returns a real <c>Max-Age</c> on its session cookie, so we set a fixed window
    /// here and re-trigger the WebView once it elapses (or sooner, if the upload
    /// response says <c>file_status=Unauthorized</c>). Seven days matches what most
    /// XFileSharing "remember me" implementations honour on the server side.
    /// </summary>
    private static readonly TimeSpan DefaultCookieLifetime = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<int, ExLoadAuthState> _authByCredentialsId = new();

    // One login at a time per credentials id — see BRuploadPipeline for the rationale.
    // Without this, kicking off N parallel uploads from the same Ex-Load account would
    // each fire its own WebView prompt.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, Task<string>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    // Same upload-action regex BRupload uses — XFileSharing renders the same template here.
    private static readonly Regex _sessIdRegex = new(
        """name=["']sess_id["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']sess_id["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Identify the file-upload form by its enctype rather than its action URL — the
    // BRupload-style /cgi-bin/upload.cgi assumption doesn't hold for every XFileSharing
    // variant (newer PHP rebuilds use upload.php / up.php / per-site custom paths).
    // multipart/form-data is the unique fingerprint: the my-files / search / URL-upload
    // forms on the same page all use application/x-www-form-urlencoded.
    //
    // Two alternatives because XFileSharing templates render attribute order
    // inconsistently (action-first vs enctype-first). The capture is in group 1 or 2 —
    // whichever matched.
    private static readonly Regex _uploadFormActionRegex = new(
        """<form\b[^>]*?(?:\baction=["']([^"']+)["'][^>]*?\benctype=["']multipart/form-data["']|\benctype=["']multipart/form-data["'][^>]*?\baction=["']([^"']+)["'])""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>DI ctor. Both dependencies are optional so existing-style tests that
    /// drive the pipeline through overrides can construct it without a real service or
    /// repo — the production paths still require both.</summary>
    public ExLoadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor mirroring <see cref="BRuploadPipeline"/>'s pattern — supplies
    /// the auth service + repo plus HTTP overrides so the pipeline can be exercised
    /// against a captured response transcript.</summary>
    internal ExLoadPipeline(
        IInteractiveAuthService authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, string> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _authService = authService;
        _loginRepository = loginRepository;
        _getOverride = url => Task.FromResult(getOverride(url));
        _uploadOverride = uploadOverride;
    }

    private sealed class AuthExpiredException : Exception { }

    public string Name => "ExLoad";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>1 GiB free-tier cap — Ex-Load shares the XFileSharing
    /// silently-closes-mid-stream behaviour. Same safety net as BRupload.</summary>
    public long? MaxFileSize => 1L * 1024 * 1024 * 1024;

    /// <summary>Same per-session file limit XFileSharing enforces across the family.</summary>
    public int? MaxFilesPerPackage => 30;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        if (MaxFileSize is long maxBytes && ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Ex-Load's {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()})",
                null);
            yield break;
        }

        // === Auth ===
        ExLoadAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out ExLoadAuthState? cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            auth = cached;
        }
        else
        {
            (ExLoadAuthState? gated, bool didAcquireCookie, string? error) = await EnsureAuthAsync(ctx, ct);

            if (didAcquireCookie)
            {
                yield return new AuthStarted();
            }

            if (gated is null)
            {
                if (didAcquireCookie)
                {
                    yield return new AuthFailed(error ?? "sign-in cancelled");
                }
                yield return new AttemptFailed(error ?? "sign-in cancelled", null);
                yield break;
            }

            if (didAcquireCookie)
            {
                yield return new AuthSucceeded();
            }

            auth = gated;
        }

        // === Upload === (mirrors BRupload bridge pattern exactly)
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        yield return new TransferStarted(ctx.FileSize);

        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<Lib.OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, auth);

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
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                // Also clear the persisted cookie so the next attempt does a fresh
                // WebView sign-in rather than re-loading the dead cookie from the DB.
                await ClearPersistedSessionAsync(ctx.Credentials, ct).ConfigureAwait(false);
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
            yield return new AuthFailed("session expired");
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

    private async Task<(ExLoadAuthState? Auth, bool DidAcquireCookie, string? Error)> EnsureAuthAsync(AttemptContext ctx, CancellationToken ct)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check in-memory cache under the gate — another caller may have refreshed
            // it while we were waiting.
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out ExLoadAuthState? cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return (cached, false, null);
            }

            // Detect proxy/pin mismatch — AttemptRunner rotates off-pin when the pinned
            // proxy is gone, which means the persisted cookie was issued from a different
            // IP than we're about to use. Invalidate it and force a fresh WebView through
            // the new proxy so the new cookie + new pin match. Also drop the in-memory
            // cache so the new auth state is rebuilt.
            if (ctx.Credentials.PinnedProxyId is int existingPin && existingPin != ctx.Proxy.Id)
            {
                ctx.Logger.Log(
                    this,
                    LogType.Status,
                    $"Ex-Load: pinned proxy {existingPin} unavailable, recovering through proxy {ctx.Proxy.Id} ({ctx.Proxy.Description}). Re-signing in.");
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                await ClearPersistedSessionAsync(ctx.Credentials, ct).ConfigureAwait(false);
            }

            // Step 1: prefer the DB-persisted cookie if it's still inside its lifetime.
            // Avoids opening the WebView on every app start.
            string? xfss;
            DateTime expiresUtc;
            bool acquiredFresh;

            if (!string.IsNullOrEmpty(ctx.Credentials.SessionCookie)
                && ctx.Credentials.SessionCookieExpiresUtc is DateTime persistedExpiry
                && persistedExpiry > DateTime.UtcNow)
            {
                xfss = ctx.Credentials.SessionCookie;
                expiresUtc = persistedExpiry;
                acquiredFresh = false;
            }
            else
            {
                if (_authService is null)
                {
                    return (null, false, "no interactive auth service available — cannot prompt for sign-in");
                }

                InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, LoginPagePath);
                string? captured;
                try
                {
                    // Route the WebView through the same proxy the runner picked for this
                    // attempt so the cookie is issued from the same IP it will be used
                    // from. XFileSharing binds sessions to the issuing IP — mismatched IPs
                    // would invalidate the cookie on the next request.
                    captured = await _authService.AcquireSessionCookieAsync(
                        spec,
                        ctx.Credentials.Username ?? string.Empty,
                        ctx.Proxy,
                        ct);
                }
                catch (Exception ex)
                {
                    return (null, true, "sign-in failed: " + ex.Message);
                }

                if (string.IsNullOrEmpty(captured))
                {
                    return (null, true, "sign-in cancelled");
                }

                xfss = captured;
                expiresUtc = DateTime.UtcNow + DefaultCookieLifetime;
                acquiredFresh = true;

                // Deliberately NOT persisting yet — wait until the upload_form scrape
                // below confirms the cookie actually authenticates. Otherwise a parse
                // failure here would leave a "valid-looking" cookie in the DB and the
                // next attempt would skip the WebView and re-hit the same error in a
                // loop. The cookie is held in `xfss` locally until step 2 succeeds.
            }

            // Step 2: scrape the per-user upload subdomain + sess_id from upload_form.
            (string? sessionId, string? actionUrl, string? formError) = await FetchUploadFormAsync(
                xfss,
                url => GetAsync(ctx, url, BuildCookieHeader(xfss)));

            if (sessionId is null || actionUrl is null)
            {
                return (null, acquiredFresh, formError ?? "upload_form parse failed");
            }

            // Form parsed → cookie is good. Now safe to persist (only for fresh sign-ins;
            // a re-use of an already-persisted cookie doesn't need a re-write).
            if (acquiredFresh)
            {
                await PersistSessionAsync(ctx.Credentials, xfss, expiresUtc, ctx.Proxy.Id, ct).ConfigureAwait(false);
            }

            ctx.Logger.Log(
                this,
                LogType.Status,
                $"Ex-Load upload_form: action={actionUrl}, sess_id={sessionId}, xfss={xfss}, expires={expiresUtc:O}");

            ExLoadAuthState newAuth = new(xfss, sessionId, actionUrl, expiresUtc);
            _authByCredentialsId[ctx.Credentials.Id] = newAuth;
            return (newAuth, acquiredFresh, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        // Ex-Load's "check" pops the WebView so the user signs in + solves the captcha
        // once. The cookie we capture is returned on the result so the Settings VM can
        // persist it (along with the pinned proxy id) onto the credentials DTO — that
        // way the first real upload doesn't pop a second WebView and uploads share the
        // sign-in's IP (XFileSharing binds sessions to the issuing IP).
        if (_authService is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in service unavailable. Restart the app and try again.");
        }

        InteractiveAuthSpec spec = new(Name, LoginUrl, CookieDomain, CookieName, LoginPagePath);
        string? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (string.IsNullOrEmpty(captured))
        {
            return new AccountCheckResult(false, AccountType.Free, "Sign-in cancelled.");
        }

        // Verify the cookie actually works by pulling /?op=upload_form and confirming
        // the form HTML parses. A cookie that doesn't authenticate redirects us back
        // to login.html, where the regex won't match. Route through _getOverride when
        // present so unit tests can stub the form response without touching the network.
        Func<string, Task<string>> get = _getOverride is not null
            ? _getOverride
            : url => handler.GetStringAsync(url, BuildCookieHeader(captured), ct);

        (string? sessionId, string? actionUrl, string? error) = await FetchUploadFormAsync(captured, get);

        if (sessionId is null || actionUrl is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "Captured cookie was not accepted by Ex-Load.");
        }

        DateTime expiresUtc = DateTime.UtcNow + DefaultCookieLifetime;
        return new AccountCheckResult(
            IsValid: true,
            AccountType: AccountType.Free,
            Message: "Signed in (Ex-Load doesn't expose premium state on this endpoint)",
            PremiumExpiry: null,
            SessionCookie: captured,
            SessionCookieExpiresUtc: expiresUtc,
            PinnedProxyId: proxy.Id);
    }

    private static async Task<(string? SessionId, string? ActionUrl, string? Error)> FetchUploadFormAsync(
        string xfss,
        Func<string, Task<string>> get)
    {
        string html;
        try
        {
            html = await get(UploadFormUrl);
        }
        catch (Exception ex)
        {
            return (null, null, "upload_form fetch failed: " + ex.Message);
        }

        Match actionMatch = _uploadFormActionRegex.Match(html);
        if (!actionMatch.Success)
        {
            // Diagnostic: tell the user what we got back so we can tell apart "bounced
            // back to login" (cookie not accepted) from "form shape doesn't match our
            // regex" (XFileSharing variant we don't know about). The snippet is the
            // first <form ...> tag we can find, or a head-of-body fallback.
            string formsFound = ExtractFormOpeningTags(html);
            string bodySnippet = Snippet(html);
            string bounce = LooksLikeLoginPage(html) ? " (looks like the server bounced back to the login/registration page — the captured cookie may not be authenticating)" : string.Empty;
            return (null, null,
                $"upload_form HTML did not contain a multipart upload form{bounce}. " +
                $"Forms found: {formsFound}. Body starts: {bodySnippet}");
        }

        // The capture is in group 1 (action-before-enctype) or group 2 (enctype-before-action),
        // depending on which alternative matched. Pick the non-empty one.
        string actionUrl = actionMatch.Groups[1].Success && actionMatch.Groups[1].Length > 0
            ? actionMatch.Groups[1].Value
            : actionMatch.Groups[2].Value;

        Match sessMatch = _sessIdRegex.Match(html);
        string? sessId = sessMatch.Success
            ? (sessMatch.Groups[1].Success ? sessMatch.Groups[1].Value : sessMatch.Groups[2].Value)
            : null;
        if (string.IsNullOrEmpty(sessId))
        {
            // Fall back to the cookie value when the form omits sess_id (mock/test fixtures).
            sessId = xfss;
        }

        return (sessId, actionUrl, null);
    }

    /// <summary>
    /// Walks the HTML and returns a compact list of every <c>&lt;form&gt;</c> opening tag
    /// found, truncated to keep the diagnostic short. Used when the upload-form regex
    /// misses so we can see at a glance what forms WERE present and what their action /
    /// enctype attributes look like.
    /// </summary>
    private static string ExtractFormOpeningTags(string html)
    {
        MatchCollection matches = Regex.Matches(html, "<form\\b[^>]*>", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return "(none)";
        }

        const int MaxTagLen = 240;
        IEnumerable<string> trimmed = matches.Cast<Match>().Take(4).Select(m =>
        {
            string tag = m.Value.Replace('\n', ' ').Replace('\r', ' ');
            return tag.Length > MaxTagLen ? tag[..MaxTagLen] + "…" : tag;
        });
        string joined = string.Join(" | ", trimmed);
        return matches.Count > 4 ? joined + $" (+{matches.Count - 4} more)" : joined;
    }

    /// <summary>
    /// Heuristic: returns true if the body looks like the login or registration page
    /// rather than a logged-in page. Used to give the user a clearer error than "regex
    /// missed" when the real cause is that the captured cookie didn't authenticate.
    /// </summary>
    private static bool LooksLikeLoginPage(string html)
    {
        // The login page contains the hCaptcha widget OR an op=login form. Either is a
        // strong signal we're not authenticated.
        return html.Contains("h-captcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("op=login", StringComparison.OrdinalIgnoreCase)
            || html.Contains("name=\"op\" value=\"login\"", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, ExLoadAuthState auth)
    {
        // Mirror the browser's request shape, just like BRupload — same XFileSharing
        // upload.cgi backend, same field set, same Origin + Sec-Fetch-* trio required
        // to dodge the "uploads not enabled for your account type" misdirection.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["sess_id"] = auth.SessionId,
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
            return await _uploadOverride(ctx.FilePath, auth.UploadActionUrl, extraFields, uploadHeaders, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            auth.UploadActionUrl,
            fileFieldName: "file_0",
            extraFields: extraFields,
            headers: uploadHeaders,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    private static Dictionary<string, string> BuildCookieHeader(string xfss)
        => new(StringComparer.Ordinal) { ["Cookie"] = "xfss=" + xfss };

    private static (string? Url, string? Error, bool AuthExpired) ParseUploadResponse(HttpResponseSnapshot response)
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

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    private Task<string> GetAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string>? headers = null)
        => _getOverride is not null
            ? _getOverride(url)
            : ctx.Handler.GetStringAsync(url, headers, ctx.Cancellation);

    private async Task PersistSessionAsync(FileHosterLoginDto credentials, string cookieValue, DateTime expiresUtc, int pinnedProxyId, CancellationToken ct)
    {
        // Mutate the live DTO so callers holding a reference see the new state, then
        // round-trip through the repo so the cookie + pinned proxy survive restarts.
        // The repo handles its own DbContext lifetime; we don't need to coordinate
        // transactions here.
        credentials.SessionCookie = cookieValue;
        credentials.SessionCookieExpiresUtc = expiresUtc;
        credentials.PinnedProxyId = pinnedProxyId;

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
            // Persistence is best-effort — a transient DB write failure shouldn't fail
            // the upload. The in-memory cache still has the cookie and the user will
            // simply re-sign-in on app restart.
        }
    }

    private async Task ClearPersistedSessionAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        // Deliberately leave PinnedProxyId in place — the cookie is dead but the proxy
        // selection might still be the right one for the next sign-in. The next
        // EnsureAuthAsync will pop the WebView, capture a fresh cookie through the
        // currently-pinned proxy (because AttemptRunner picked it via the pin), and
        // PersistSessionAsync re-writes the pin to the same value.

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
            // Same best-effort rationale as PersistSessionAsync.
        }
    }

    private sealed class UploadResult
    {
        [JsonPropertyName("file_code")] public string? Code { get; set; }

        [JsonPropertyName("file_status")] public string? Status { get; set; }
    }
}
