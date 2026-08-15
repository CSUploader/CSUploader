// <copyright file="XFileSharingApiPipeline.AccountCheck.cs" company="CSUploader">
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
/// Account verification (API-key and web-form paths) and the storage refresh, as a partial: one
/// class across five files, one concern each (see the main file's class doc).
/// </summary>
public abstract partial class XFileSharingApiPipeline
{
    /// <summary>
    /// Account verification for web-form (no-API) hosters: WebView sign-in to capture the <c>xfss</c>
    /// cookie, then an HTML scrape of <see cref="WebFormAccountPageUrl"/> for logged-in confirmation,
    /// the username, and storage usage. No API key is involved; the persisted credential is the session cookie (reused
    /// by <see cref="RunWebFormAsync"/> and by the non-interactive storage refresh). Quota is always
    /// null — these hosters don't advertise a cap, so the grid's Available cell shows "Unlimited".
    /// </summary>
    private async Task<AccountCheckResult> CheckAccountViaWebFormAsync(string username, string password, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        // A hoster whose login this app can post itself never needs the browser — see SupportsDirectLogin.
        if (SupportsDirectLogin)
        {
            (string? xfss, string? loginError) = await DirectLoginAsync(handler, username, password, ct).ConfigureAwait(false);
            return xfss is null
                ? new AccountCheckResult(false, AccountType.Free, loginError ?? $"{Name} sign-in failed.")
                : await ReadWebFormAccountAsync(xfss, username, handler, proxy, freshSignIn: true, ct).ConfigureAwait(false);
        }

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

        return await ReadWebFormAccountAsync(ComposeStoredSession(auth), username, handler, proxy, freshSignIn: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <see cref="WebFormAccountPageUrl"/> with a session cookie and turns it into a check
    /// result: logged-in confirmation, the account name, and storage usage. Shared by the sign-in
    /// path (with a freshly captured cookie) and by <see cref="RefreshAccountAsync"/> (with the
    /// STORED one) — the same page says the same things either way, and having one reader means a
    /// re-check can't disagree with the sign-in that preceded it.
    /// </summary>
    /// <param name="freshSignIn">True right after an interactive sign-in. Only changes the wording
    /// when the page doesn't look logged-in: a brand-new cookie failing means the sign-in didn't
    /// complete, while a stored one failing means the session has simply run out.</param>
    private async Task<AccountCheckResult> ReadWebFormAccountAsync(
        string storedSession,
        string? typedUsername,
        HttpHandler handler,
        Lib.Net.ProxyChoice proxy,
        bool freshSignIn,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(storedSession);
        string html;
        string finalUrl;
        int hops;
        try
        {
            (html, finalUrl, hops) = await FetchMyAccountAsync(handler, WebFormAccountPageUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "Account page fetch failed: " + ex.Message);
        }

        if (!LooksSignedIn(html))
        {
            string trail = hops > 0 ? $" after following {hops} redirect(s) to {finalUrl}" : string.Empty;
            string summary = freshSignIn
                ? $"Signed in, but the account page didn't load as logged-in{trail}. The sign-in may not have completed."
                : $"The saved session is no longer valid{trail} — sign in again.";
            return new AccountCheckResult(false, AccountType.Free, summary, Detail: BuildFailureDetail(summary, html));
        }

        string? scrapedUsername = ParseAccountUsername(html);
        (long? used, long? quota) = ParseStorageUsage(html);

        return new AccountCheckResult(
            IsValid: true,
            AccountType: AccountType.Free,
            Message: "Signed in (Free)",
            SessionCookie: storedSession,
            SessionCookieExpiresUtc: DateTime.UtcNow + SignInSessionLifetime,
            PinnedProxyId: proxy.Id,
            DerivedUsername: scrapedUsername ?? (string.IsNullOrEmpty(typedUsername) ? null : typedUsername),
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>
    /// Re-checks an account WITHOUT opening the sign-in browser, using the credential already stored.
    /// <para>
    /// This is the difference between adding an account once and being asked to sign in twice: every
    /// save runs a verification pass, and until this existed only HitFile implemented the contract, so
    /// a session-cookie hoster re-opened its sign-in window seconds after the user had signed in.
    /// </para>
    /// <para>
    /// An API key is the durable credential and validates over the API with no browser involved; a
    /// web-form hoster re-reads its account page with the stored cookie. A cookie that has expired
    /// reports invalid and says to sign in again — for these hosters the cookie IS the credential, so
    /// silently reopening a browser (the old behaviour) hid the fact that the account had lapsed.
    /// </para>
    /// </summary>
    public virtual async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            // The API path validates a key directly and never opens a browser.
            return await CheckAccountAsync(string.Empty, string.Empty, apiKey, handler, proxy, ct).ConfigureAwait(false);
        }

        if (!UsesWebFormUpload || string.IsNullOrEmpty(sessionCookie))
        {
            return await CheckAccountAsync(string.Empty, string.Empty, apiKey, handler, proxy, ct).ConfigureAwait(false);
        }

        AccountCheckResult refreshed = await ReadWebFormAccountAsync(
            sessionCookie, typedUsername: null, handler, proxy, freshSignIn: false, ct).ConfigureAwait(false);

        // A lapsed cookie means different things depending on what the credential IS. Where the cookie
        // is all there is, the account really can't upload until the user signs in again. Where a
        // username and password are stored, the account is fine and the next upload just signs in
        // again — so don't report a failure the user can neither see the cause of nor act on.
        return !refreshed.IsValid && SupportsDirectLogin
            ? new AccountCheckResult(true, AccountType.Free, "Signed in (Free)")
            : refreshed;
    }

    /// <summary>
    /// Non-interactive storage refresh for web-form hosters: GET <see cref="WebFormAccountPageUrl"/>
    /// with the STORED <c>xfss</c> cookie (never a WebView) and scrape it for used + quota. Returns null
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
            (html, _, _) = await FetchMyAccountAsync(handler, WebFormAccountPageUrl, cookieHeader, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (!LooksSignedIn(html))
        {
            return null;
        }

        (long? used, long? quota) = ParseStorageUsage(html);
        return used is null && quota is null ? null : new StorageUsage(used, quota);
    }

    /// <summary>True when a fetched logged-in page (<c>my_account</c> / <c>my_files</c>) carries a
    /// logout link. A logged-out fetch lands on the login page, which has none.</summary>
    private static bool LooksLoggedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a fetched account page shows us as signed in. The default looks for the family's
    /// <c>?op=logout</c> link; forks with rewritten routes override it (DDownload's dashboard links
    /// plain <c>/logout</c>). Getting this wrong rejects a perfectly good sign-in with "the account
    /// page didn't load as logged-in", so check it against the real page before trusting the default.
    /// </summary>
    protected virtual bool LooksSignedIn(string html) => LooksLoggedIn(html);

    /// <summary>
    /// Why this hoster will refuse <paramref name="ctx"/>'s file before a byte is sent, or null when
    /// it will take it. Default: nothing to check.
    /// <para>
    /// Exists because this family can reject a file at the LAST step. Uploadrar publishes an
    /// extension blocklist (<c>?op=api_get_limits</c> → <c>ExtNotAllowed</c>) but only enforces it in
    /// <c>import_file</c>, so a blocked file uploads in full and is then thrown away — observed in a
    /// capture where a 5 MB .avi transferred fine and the finalise answered
    /// <c>{"error":"unallowed extension"}</c>. Checking locally costs nothing and reports the real
    /// reason immediately.
    /// </para>
    /// </summary>
    protected virtual string? PreflightRejection(AttemptContext ctx)
        // The character rule is a default interface method (no XFS host overrides it today), so it has
        // to be called through the interface; the extension rule is a virtual on this base.
        => ((IFileHosterPipeline)this).RejectedFileNameReason(ctx.FileName)
           ?? RejectedFileExtensionReason(ctx.FileName);

    /// <summary>
    /// Why this hoster's server would refuse a file's TYPE, or null. Overriding this is enough for both
    /// consumers: <see cref="PreflightRejection"/> consults it (so the upload fails fast before sending
    /// bytes), and the upload WIZARD calls it through
    /// <see cref="IFileHosterPipeline.RejectedFileExtensionReason"/> to drop such files from this
    /// hoster's Summary column and name them in the warning panel before the user presses Next.
    /// <para>
    /// One rule, two consumers, deliberately: an extension list duplicated across an upload-time check
    /// and a wizard-time check is a list that eventually disagrees with itself.
    /// </para>
    /// </summary>
    public virtual string? RejectedFileExtensionReason(string fileName)
    {
        _ = fileName;
        return null;
    }

    private static string? ExtractMyAccountUsername(string html)
    {
        Match m = _myAccountUsernameRegex.Match(html);
        if (m.Success && m.Groups[1].Length > 0)
        {
            return m.Groups[1].Value;
        }

        m = _usernameRowRegex.Match(html);
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
            m = _freeSpaceBarRegex.Match(html);
        }

        if (!m.Success)
        {
            m = _usedSpaceRowRegex.Match(html);
        }

        if (!m.Success)
        {
            return (null, null);
        }

        // The table row states the used figure in the QUOTA's unit ("0.00 of 500 GB"); the two bar
        // shapes always carry both. An explicit unit therefore wins wherever one is present.
        string quotaUnit = m.Groups[4].Value;
        string usedUnit = m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : quotaUnit;

        return (ParseSizeToBytes(m.Groups[1].Value, usedUnit),
                ParseSizeToBytes(m.Groups[3].Value, quotaUnit));
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
        // API-mode doesn't validate the password — sign-in goes through the WebView captcha. A
        // SupportsDirectLogin hoster DOES use it, below.
        _ = password;

        // Web-form (no-API) hosters: there's no API key to validate and no /api/account/info to call.
        // Sign in via WebView and read identity/storage from the my_files HTML instead.
        if (UsesWebFormUpload)
        {
            return await CheckAccountViaWebFormAsync(username, password, handler, proxy, ct);
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

    /// <summary>
    /// Asks the API for an upload node, retrying when the answer is infrastructure noise rather than
    /// the API speaking.
    /// <para>
    /// Observed on Data Vaults: Cloudflare answered <c>520</c> with the body <c>error code: 520</c>
    /// while the same key worked on eight consecutive calls seconds later. Without a retry that
    /// momentary edge failure fails the user's file outright, since a yielded AttemptFailed is
    /// terminal — <c>AttemptRunner</c> only re-runs on its two never-double-create faults.
    /// </para>
    /// <para>
    /// Only UNREADABLE answers are retried. A JSON verdict — "Wrong auth", a 403, a non-200 status —
    /// is the API's decision and gets none, so a bad key still fails on the first call.
    /// </para>
    /// </summary>
    private async Task<(string? SessId, string? UploadUrl, string? Error, bool AuthExpired)> GetUploadServerAsync(string apiKey, AttemptContext ctx, CancellationToken ct)
    {
        (string? SessId, string? UploadUrl, string? Error, bool AuthExpired) result = default;

        for (int attempt = 1; ; attempt++)
        {
            bool transient;
            (result, transient) = await TryGetUploadServerOnceAsync(apiKey, ctx, ct).ConfigureAwait(false);

            if (!transient || attempt >= UploadServerAttempts)
            {
                return result;
            }

            ctx.Logger.Log(this, LogType.Status, $"{Name}: upload-server lookup got no usable answer ({result.Error}); retrying ({attempt + 1} of {UploadServerAttempts}).");
            await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
        }
    }

    private async Task<((string? SessId, string? UploadUrl, string? Error, bool AuthExpired) Result, bool Transient)> TryGetUploadServerOnceAsync(string apiKey, AttemptContext ctx, CancellationToken ct)
    {
        string url = $"{ApiUploadServerUrl}?key={Uri.EscapeDataString(apiKey)}";
        string body;
        try
        {
            body = await GetAsync(ctx, url, headers: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A dropped connection or timeout is the same class of problem as a 520.
            return ((null, null, "upload/server request failed: " + ex.Message, false), true);
        }

        UploadServerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<UploadServerResponse>(body);
        }
        catch
        {
            return ((null, null, $"upload/server: response was not valid JSON: {Snippet(body)}", false), true);
        }

        if (response is null)
        {
            return ((null, null, $"upload/server: empty response: {Snippet(body)}", false), true);
        }

        return (GetUploadServerVerdict(response), false);
    }

    /// <summary>The API's own answer, once we know we're reading the API and not an edge error.</summary>
    private (string? SessId, string? UploadUrl, string? Error, bool AuthExpired) GetUploadServerVerdict(UploadServerResponse response)
    {
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

}
