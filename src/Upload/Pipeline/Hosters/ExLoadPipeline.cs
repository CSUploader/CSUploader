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
/// Ex-Load upload pipeline. Ex-Load exposes a REST API with a per-account key, so the
/// primary credential is an <see cref="FileHosterLoginDto.ApiKey"/>. Two ways to acquire
/// one:
/// <list type="bullet">
///   <item><b>API-key direct</b>: the user pastes their key into the Add Account dialog.
///   No WebView, no cookie, no captcha — verification is a single
///   <c>/api/account/info?key=...</c> round-trip and uploads use <c>/api/upload/server</c>
///   directly.</item>
///   <item><b>Username/password bootstrap</b>: the user types credentials. The pipeline
///   pops <see cref="IInteractiveAuthService"/> to capture an <c>xfss</c> cookie past the
///   hCaptcha login, GETs <c>/?op=my_account</c>, scrapes the <c>api-url</c> input for
///   the existing API key, or follows the <c>generate_api_key</c> link if none exists,
///   then persists the key onto the credentials DTO. The cookie + pinned proxy are
///   discarded after this one-shot — subsequent uploads are pure API.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Upload itself: <c>GET /api/upload/server?key=...</c> returns
/// <c>{sess_id, result: "http://fsNN.ex-load.com/cgi-bin/upload.cgi"}</c>. We POST a
/// multipart body to that URL with <c>sess_id</c> + <c>file_0</c>; the response is the
/// standard XFileSharing <c>[{file_code, file_status}]</c> JSON array, same shape
/// BRupload returns.
/// </para>
/// <para>
/// Because the API key is the credential (not a session cookie bound to an IP), uploads
/// can rotate proxies freely. The <see cref="FileHosterLoginDto.PinnedProxyId"/> is only
/// used during the bootstrap window and cleared once we have the API key.
/// </para>
/// </remarks>
public sealed class ExLoadPipeline : IFileHosterPipeline
{
    private const string Host = "https://ex-load.com";
    private const string LoginUrl = Host + "/login.html";
    private const string MyAccountUrl = Host + "/?op=my_account";
    private const string PublicUrlPrefix = Host + "/";
    private const string CookieName = "xfss";
    private const string CookieDomain = ".ex-load.com";
    private const string LoginPagePath = "/login.html";

    // API endpoints. Ex-Load follows the same XFileSharing API convention as several other
    // hosters in this family — key as a query parameter, JSON responses with
    // {msg, status, server_time, result, ...}.
    private const string ApiAccountInfoUrl = Host + "/api/account/info";
    private const string ApiUploadServerUrl = Host + "/api/upload/server";

    /// <summary>
    /// Cookie lifetime applied during the U/P bootstrap window. XFileSharing rarely
    /// returns a real <c>Max-Age</c>; seven days is the standard "remember me" honour
    /// horizon on the server side. Once the bootstrap completes we throw the cookie
    /// away anyway, so this only matters when a user signs in via U/P but cancels the
    /// my_account scrape — the next attempt can re-use the cookie within this window.
    /// </summary>
    private static readonly TimeSpan DefaultCookieLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// One bootstrap at a time per credentials id — prevents N parallel uploads on a
    /// brand-new account from all popping their own WebView.
    /// </summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _bootstrapGates = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<string>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    // Hidden-input regex for the CSRF token on the my_account page. Same shape XFileSharing
    // renders for any of its `token` fields — handles attribute order variation.
    private static readonly Regex _csrfTokenRegex = new(
        """name=["']token["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']token["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The API key is embedded inside the value of a read-only input named "api-url":
    //   <input type="text" readonly name="api-url" value="https://ex-load.com/api/account/info?key=10159312numt5ftnc6m47a6g">
    // We extract the `key=` query parameter from the value attribute. Tolerant of
    // attribute-order variation (api-url could come before or after the value attribute).
    private static readonly Regex _apiKeyRegex = new(
        """name=["']api-url["'][^>]*?value=["'][^"']*[?&]key=([^"'&]+)["']|value=["'][^"']*[?&]key=([^"'&]+)["'][^>]*?name=["']api-url["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>DI ctor.</summary>
    public ExLoadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor — supplies overrides for the GET (my_account, generate, /api/account/info,
    /// /api/upload/server) and upload calls so the pipeline can be driven against canned responses.</summary>
    internal ExLoadPipeline(
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

    public string Name => "ExLoad";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>1 GiB free-tier cap — same XFileSharing mid-stream-close behaviour as BRupload.</summary>
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
            // The API key the server gave us was rejected (e.g. user regenerated it).
            // Clear and force a re-bootstrap on the next attempt.
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

    /// <summary>
    /// Returns the API key for this account, performing a U/P-mode bootstrap (WebView +
    /// my_account scrape) if necessary. <paramref name="DidBootstrap"/> is true when this
    /// call ran the bootstrap path so callers can emit Auth* events appropriately.
    /// </summary>
    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> EnsureApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        // Fast path: API key already on credentials. No bootstrap, no cookie, no WebView.
        if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
        {
            return (ctx.Credentials.ApiKey, false, null);
        }

        // No API key yet — we need to bootstrap one from the U/P credentials. Gate so
        // concurrent attempts on the same brand-new account share a single bootstrap.
        SemaphoreSlim gate = _bootstrapGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check under the gate.
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

    /// <summary>
    /// The U/P-mode bootstrap: capture an <c>xfss</c> cookie via WebView (or reuse a
    /// non-expired persisted one), GET my_account, scrape the API key — generating one
    /// if the account doesn't have one yet — then persist the key and clear the now-
    /// unnecessary cookie/pin.
    /// </summary>
    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> BootstrapApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        // Step 1: get an xfss cookie. Prefer the persisted one if still valid; otherwise
        // pop the WebView through the runner-supplied proxy (so the cookie is issued
        // from the same IP we'd use for the my_account GET below — XFileSharing binds
        // session cookies to the issuing IP).
        string? xfss = await GetOrAcquireXfssCookieAsync(ctx, ct);
        if (xfss is null)
        {
            return (null, true, "sign-in cancelled or no usable proxy available");
        }

        // Step 2: GET my_account. Read it once, try to extract the existing API key.
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

        // Step 3: if the page doesn't render an api-url input yet, follow the generate
        // link to create one, then re-fetch and re-scrape.
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
                // The generate endpoint replies with a 302 + a "msg=New API key generated"
                // cookie and redirects back to my_account. We don't care about the body
                // of the redirect — we re-fetch my_account ourselves and look for the key.
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

        // Step 4: persist the API key and clear the now-unnecessary cookie + pin (the
        // API key is IP-agnostic, no need to keep the rotation locked).
        await PersistApiKeyAsync(ctx.Credentials, apiKey, ct).ConfigureAwait(false);

        ctx.Logger.Log(this, LogType.Status, $"Ex-Load: bootstrapped API key for {ctx.Credentials.Username}");
        return (apiKey, true, null);
    }

    /// <summary>
    /// Returns a usable <c>xfss</c> cookie for the U/P bootstrap. Prefers the DB-cached
    /// cookie when it's non-expired and was issued from the same proxy we're using
    /// now; otherwise pops the WebView.
    /// </summary>
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

        // Stash on the DTO so a bootstrap failure mid-flow doesn't force a second WebView
        // on the very next retry. Pin to the current proxy for the same IP-binding reason.
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
        _ = password; // Ex-Load doesn't validate the password field — sign-in goes through the WebView captcha.

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
                ApiKey: apiKey);
        }

        // Otherwise we're in U/P mode — bootstrap an API key via WebView + my_account scrape.
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

        // Local rename to avoid shadowing the apiKey parameter (which is null here in
        // the U/P branch but in scope for the whole method).
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

        // Verify the key actually works by hitting account/info — gives us premium
        // expiry as a bonus. (Locals named derivedInfo / derivedType / derivedMessage
        // to avoid shadowing the corresponding API-key-path locals above.)
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
            ApiKey: derivedKey);
    }

    /// <summary>
    /// GET <c>/api/upload/server?key=...</c> and parse the per-upload sess_id + upload
    /// subdomain URL. Returns <c>AuthExpired=true</c> when the API rejects the key.
    /// </summary>
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

        return (response.SessId, response.Result, null, false);
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        // Same multipart shape BRupload uses — XFileSharing's upload.cgi reads the same
        // fields whether we got there via the web form (BRupload) or the API
        // (Ex-Load). Origin + Sec-Fetch-* are kept for symmetry; the API path may not
        // need them but they're cheap and shouldn't hurt.
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

    private static string? ExtractApiKey(string html)
    {
        Match m = _apiKeyRegex.Match(html);
        if (!m.Success) return null;
        string captured = m.Groups[1].Success && m.Groups[1].Length > 0
            ? m.Groups[1].Value
            : m.Groups[2].Value;
        return string.IsNullOrEmpty(captured) ? null : captured;
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
        // Save the key onto the live DTO and clear the cookie/pin (we no longer need
        // either — the API key works from any IP and doesn't expire on a short timer).
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
            // Best-effort persist — the in-memory DTO mutation still keeps the key
            // available for the rest of this session.
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
