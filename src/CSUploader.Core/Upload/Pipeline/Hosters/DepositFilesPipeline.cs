// <copyright file="DepositFilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DepositFiles (depositfiles.com) — <b>ACCOUNT-ONLY</b>, 10 GiB per file, on a small JSON API:
/// <list type="number">
///   <item><b>Node.</b> <c>GET /api/upload/regular</c> under the session cookie →
///   <c>{"data":{"upload_url":"https://fileshareNNNN.depositfiles.com/FSNNN-1u/?X-Progress-ID=…",
///   "max_file_size_mb":"10240"}}</c>.</item>
///   <item><b>Upload.</b> One multipart POST to that node carrying <c>files</c>, <c>format=html5</c>,
///   <b><c>member_passkey</c></b>, <c>fm</c> and <c>fmh</c> →
///   <c>{"status":"OK","download_url":…,"delete_url":…}</c>. The target is the node URL with its query
///   stripped and <c>/FS</c> rewritten to <c>/upload/FS</c> — the site's own script does exactly that
///   (<c>uploadScript: uploadURL.replace('/FS', '/upload/FS')</c>).</item>
/// </list>
/// <para>
/// <b>⚠ The passkey is what files the upload under the account, and omitting it fails SILENTLY.</b>
/// Proven live: an upload posted with an empty <c>member_passkey</c> — but with a perfectly good
/// session cookie — still answers <c>{"status":"OK"}</c> with a working download link, and the file is
/// simply absent from the account's own <c>/api/file/listing</c>. Same shape of trap as FileMirage's
/// wrong bearer token. So this pipeline refuses to upload without one rather than producing a link the
/// user's account doesn't own.
/// </para>
/// <para>
/// <b>Anonymous upload does not exist here</b> — <c>/api/upload/regular</c> answers a caller with no
/// session <c>{"status":"Error","error":"LoginInvalid","error_code":101}</c>, and the signed-out
/// upload page says so in words ("All you need is to create an account and upload files").
/// </para>
/// <para>
/// <b>⚠ Signing in is captcha-gated, but only sometimes.</b> <c>POST /api/user/login</c> takes a plain
/// username and password (its four captcha fields are posted empty by the site itself) and works —
/// until the host decides otherwise, at which point the SAME request answers
/// <c>{"error":"CaptchaRequired","error_code":104}</c>. It is risk-triggered, not per-login: several
/// sign-ins succeeded before the wall appeared. Cloudflare Turnstile is what it wants
/// (<c>FEATURE_CLOUDFLARE_TURNSTILE_ENABLED</c> on the login page). So this tries the direct login
/// first — no window, no friction — and falls back to the embedded browser only when the host asks
/// for the captcha. <b>The upload itself is never captcha-gated</b>, and the session cookie is issued
/// with <c>Max-Age=31536000</c>, so that fallback is a once-a-year event at worst.
/// </para>
/// <para>
/// <b>Files expire.</b> The account's listing stamps every file with <c>dt_expires</c> 121 days after
/// <c>dt_added</c> on the free tier — long, but not permanent, and the user should know before they
/// pick this host for an archive.
/// </para>
/// </summary>
public sealed class DepositFilesPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://depositfiles.com";
    private const string LoginApiUrl = Host + "/api/user/login";
    private const string LoginPageUrl = Host + "/login.php";
    private const string UploadPageUrl = Host + "/?upload=1";
    private const string NodeApiUrl = Host + "/api/upload/regular";

    /// <summary>The upload form's own <c>MAX_FILE_SIZE</c>, and <c>max_file_size_mb: "10240"</c> from
    /// the node call.</summary>
    private const long MaxFileSizeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>The one cookie that authenticates: asked with only <c>autologin</c> the node call
    /// answers normally, and with only the <c>al_&lt;hash&gt;</c> cookie it answers LoginInvalid.</summary>
    private const string SessionCookieName = "autologin";

    /// <summary>What the login sets it for: <c>Max-Age=31536000</c>.</summary>
    private const int SessionLifetimeDays = 365;

    /// <summary>Its own error code for "solve a captcha", as opposed to 101 = not signed in / wrong
    /// password.</summary>
    private const int CaptchaRequiredCode = 104;

    /// <summary>The account's durable upload key, rendered on the upload page as
    /// <c>&lt;div id="container_upload" sharedkey="…"&gt;</c>. Also returned by the login API as
    /// <c>member_passkey</c> — this is how the WebView path, which never sees that JSON, still gets it.</summary>
    private static readonly Regex SharedKeyRegex = new(
        """sharedkey\s*=\s*["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _uploadOverride;

    public DepositFilesPipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — stubs the page GETs, the API form POSTs and the file upload.</summary>
    internal DepositFilesPipeline(
        IInteractiveAuthService? authService,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? uploadOverride = null)
    {
        _authService = authService;
        _getOverride = getOverride;
        _postFormOverride = postFormOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "DepositFiles";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Measured, not read off the page: the node call refuses a caller with no session.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed(
                "DepositFiles has no anonymous upload — its upload node refuses a caller with no account. "
                + "Add a DepositFiles account in Account Manager.",
                null);
            yield break;
        }

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds DepositFiles' {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        string? session = NullIfWhiteSpace(ctx.Credentials.SessionCookie);
        if (session is null)
        {
            yield return new AttemptFailed(
                "The DepositFiles account has no saved sign-in. Re-check it in Account Manager — signing in "
                + "may need the embedded browser if the host asks for a captcha.",
                null);
            yield break;
        }

        // The passkey, not the cookie, is what puts the file on the account — and without it the
        // upload SUCCEEDS anyway and the file belongs to nobody. Never guess it.
        (string? passkey, string? keyError) = await ResolvePasskeyAsync(ctx, session);
        if (passkey is null)
        {
            yield return new AttemptFailed(keyError!, null);
            yield break;
        }

        (string? node, string? nodeError) = await GetUploadNodeAsync(ctx, session);
        if (node is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, node, passkey);
        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        HttpResponseSnapshot? response = null;
        Exception? transferFault = null;
        try
        {
            response = await uploadTask;
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            transferFault = ex;
        }

        if (transferFault is not null)
        {
            yield return new AttemptFailed($"DepositFiles upload failed: {transferFault.Message}", transferFault);
            yield break;
        }

        (string? link, string? deleteLink, string? uploadError) = ParseUploadResponse(response!);
        if (link is null)
        {
            yield return new AttemptFailed(uploadError!, null);
            yield break;
        }

        // The only handle on the file besides the account page, so it is logged rather than dropped.
        if (deleteLink is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} can be deleted at {deleteLink}");
        }

        yield return new TransferCompleted(link);
    }

    /// <summary>
    /// The stored passkey, else scraped off the upload page under the session. Deliberately NOT
    /// optional: see the class remarks — an upload without it succeeds and lands under no account.
    /// </summary>
    private async Task<(string? Passkey, string? Error)> ResolvePasskeyAsync(AttemptContext ctx, string session)
    {
        if (NullIfWhiteSpace(ctx.Credentials.ApiKey) is { } stored)
        {
            return (stored, null);
        }

        HttpResponseSnapshot page;
        try
        {
            page = await GetAsync(ctx.Handler, UploadPageUrl, PageHeaders(session), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"DepositFiles upload page fetch failed: {ex.Message}");
        }

        return ParseSharedKey(page) is { } key
            ? (key, null)
            : (null, "DepositFiles didn't hand out this account's upload key — the saved sign-in has "
                + "probably expired. Re-check the account in Account Manager.");
    }

    /// <summary>Reads <c>sharedkey</c> off the upload page. Internal for testing.</summary>
    internal static string? ParseSharedKey(HttpResponseSnapshot page)
        => page.StatusCode is >= 200 and < 300 && SharedKeyRegex.Match(page.Body) is { Success: true } m
            ? NullIfWhiteSpace(m.Groups[1].Value)
            : null;

    private async Task<(string? Node, string? Error)> GetUploadNodeAsync(AttemptContext ctx, string session)
    {
        HttpResponseSnapshot response;
        try
        {
            response = await GetAsync(ctx.Handler, NodeApiUrl, ApiHeaders(session), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"DepositFiles upload-node lookup failed: {ex.Message}");
        }

        return ParseUploadNode(response);
    }

    /// <summary>
    /// Turns the node reply into the URL the file is POSTed to, applying the site's own two-step
    /// rewrite: drop the query (the <c>X-Progress-ID</c> belongs to its progress poller, which this
    /// app doesn't use), then <c>/FS</c> → <c>/upload/FS</c>. Internal for testing.
    /// </summary>
    internal static (string? Node, string? Error) ParseUploadNode(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"DepositFiles wouldn't name an upload node (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"DepositFiles' node lookup wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadApiError(root) is { } apiError)
        {
            // 101 here means the session is gone, which the user can act on; anything else is quoted.
            return (null, apiError.Code == 101
                ? "The saved DepositFiles sign-in is no longer valid — re-check the account in Account Manager."
                : $"DepositFiles refused the upload-node lookup: {apiError.Error}");
        }

        if (!root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("upload_url", out JsonElement urlElement)
            || urlElement.GetString() is not { Length: > 0 } uploadUrl)
        {
            return (null, $"DepositFiles' node lookup carried no upload_url: {Snippet(response.Body)}");
        }

        int query = uploadUrl.IndexOf('?', StringComparison.Ordinal);
        string node = query >= 0 ? uploadUrl[..query] : uploadUrl;

        int marker = node.IndexOf("/FS", StringComparison.Ordinal);
        if (marker < 0)
        {
            return (null, $"DepositFiles' upload node wasn't the expected /FS… path: {node}");
        }

        return (node[..marker] + "/upload" + node[marker..], null);
    }

    private Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string node, string passkey)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["format"] = "html5",

            // What files the upload under the account. Empty = a working link owned by nobody.
            ["member_passkey"] = passkey,
            ["fm"] = "_root",
            ["fmh"] = string.Empty,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        return _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, node, fields)
            : ctx.Handler.UploadMultipartAsync(
                ctx.FilePath, node, "files", fields, headers, ctx.SpeedLimitProvider, ctx.Cancellation);
    }

    /// <summary>Reads the upload reply — the share link and the delete link. Internal for testing.</summary>
    internal static (string? Link, string? DeleteLink, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"DepositFiles rejected the upload (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, null, $"DepositFiles' upload reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadApiError(root) is { } apiError)
        {
            return (null, null, $"DepositFiles refused the upload: {apiError.Error}");
        }

        if (!root.TryGetProperty("download_url", out JsonElement dl) || dl.GetString() is not { Length: > 0 } link)
        {
            return (null, null, $"DepositFiles took the file but returned no link: {Snippet(response.Body)}");
        }

        string? delete = root.TryGetProperty("delete_url", out JsonElement del) ? NullIfWhiteSpace(del.GetString()) : null;

        // Its API answers with http:// links; the site serves https and 301s the plain one.
        return (ToHttps(link), ToHttps(delete), null);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new AccountCheckResult(false, AccountType.Free, "DepositFiles needs the account's username and password.");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["login"] = username,
            ["password"] = password,

            // Posted empty, exactly as the site's own login page posts them when it isn't asking for
            // a captcha. Sending the fields matters; the values are genuinely blank.
            ["recaptcha_challenge_field"] = string.Empty,
            ["recaptcha_response_field"] = string.Empty,
            ["g-recaptcha-response"] = string.Empty,
            ["cf-turnstile-response"] = string.Empty,
        };

        HttpResponseSnapshot login;
        try
        {
            login = await PostFormAsync(handler, LoginApiUrl, form, ApiHeaders(null), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "DepositFiles sign-in failed: " + ex.Message);
        }

        (string? session, string? passkey, string? name, AccountType type, int? errorCode, string? errorMessage) = ParseLogin(login);

        if (session is not null && passkey is not null)
        {
            return new AccountCheckResult(
                true,
                type,
                "Signed in to DepositFiles.",
                SessionCookie: session,
                SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),
                ApiKey: passkey,
                DerivedUsername: name ?? username);
        }

        // Risk-triggered, not per-login: the same request worked minutes earlier. A human solving it
        // once in the embedded browser buys a session good for a year.
        if (errorCode == CaptchaRequiredCode)
        {
            return await SignInInteractivelyAsync(username, handler, proxy, ct);
        }

        return new AccountCheckResult(false, AccountType.Free, errorMessage ?? "DepositFiles rejected the sign-in — check the username and password.");
    }

    /// <summary>
    /// Reads the login reply: the session cookie, the account's passkey, its name and tier, or the
    /// error the host answered with. Internal for testing.
    /// </summary>
    internal static (string? Session, string? Passkey, string? Name, AccountType Type, int? ErrorCode, string? ErrorMessage) ParseLogin(HttpResponseSnapshot response)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, null, null, AccountType.Free, null, $"DepositFiles' sign-in reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadApiError(root) is { } apiError)
        {
            string message = apiError.Code == CaptchaRequiredCode
                ? "DepositFiles is asking for a captcha."
                : "DepositFiles rejected the sign-in — check the username and password.";
            return (null, null, null, AccountType.Free, apiError.Code, message);
        }

        if (!root.TryGetProperty("data", out JsonElement data))
        {
            return (null, null, null, AccountType.Free, null, $"DepositFiles' sign-in reply carried no account: {Snippet(response.Body)}");
        }

        string? passkey = data.TryGetProperty("member_passkey", out JsonElement pk) ? NullIfWhiteSpace(pk.GetString()) : null;
        string? name = data.TryGetProperty("username", out JsonElement un) ? NullIfWhiteSpace(un.GetString()) : null;

        // "free" is the only tier seen; anything else is reported as paid rather than guessed at in
        // detail, and nothing in this pipeline changes behaviour on it.
        AccountType type = data.TryGetProperty("mode", out JsonElement mode)
            && !string.Equals(mode.GetString(), "free", StringComparison.OrdinalIgnoreCase)
            ? AccountType.Premium
            : AccountType.Free;

        string? session = ReadSessionCookie(response);

        return session is null || passkey is null
            ? (null, null, null, AccountType.Free, null, "DepositFiles signed in but issued no usable session.")
            : (session, passkey, name, type, null, null);
    }

    /// <summary>Pulls <c>autologin</c> out of the login reply. Internal for testing.</summary>
    internal static string? ReadSessionCookie(HttpResponseSnapshot response)
    {
        foreach (string cookie in response.SetCookies)
        {
            if (cookie.StartsWith(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase))
            {
                string value = cookie.Split(';', 2)[0][(SessionCookieName.Length + 1)..];
                if (!string.IsNullOrWhiteSpace(value) && value != "deleted")
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>The captcha fallback: a human signs in in the embedded browser, then the passkey is
    /// scraped off the upload page — the login JSON that normally carries it never happens here.</summary>
    private async Task<AccountCheckResult> SignInInteractivelyAsync(string username, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "DepositFiles is asking for a captcha, which needs the desktop app's embedded browser. Try again from the app.");
        }

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(
                new InteractiveAuthSpec(
                    HosterName: Name,
                    LoginUrl: LoginPageUrl,
                    CookieDomain: ".depositfiles.com",
                    CookieName: SessionCookieName,
                    CaptureOnlyAfterLeavingLoginPage: true),
                username,
                proxy,
                ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "DepositFiles sign-in failed: " + ex.Message);
        }

        if (NullIfWhiteSpace(captured?.SessionCookieValue) is not { } session)
        {
            return new AccountCheckResult(false, AccountType.Free, "DepositFiles sign-in was cancelled, or didn't complete before the window was closed.");
        }

        HttpResponseSnapshot page;
        try
        {
            page = await GetAsync(handler, UploadPageUrl, PageHeaders(session), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "DepositFiles upload page fetch failed: " + ex.Message);
        }

        return ParseSharedKey(page) is { } passkey
            ? new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in to DepositFiles.",
                SessionCookie: session,
                SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),
                ApiKey: passkey)
            : new AccountCheckResult(false, AccountType.Free, "DepositFiles signed in but wouldn't hand out this account's upload key.");
    }

    /// <summary>Re-checks the stored session without a password — and re-reads the passkey, which is
    /// the credential the upload actually needs.</summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        try
        {
            HttpResponseSnapshot page = await GetAsync(handler, UploadPageUrl, PageHeaders(sessionCookie), ct);

            if (ParseSharedKey(page) is { } passkey)
            {
                return new AccountCheckResult(
                    true,
                    AccountType.Free,
                    "Signed in to DepositFiles.",
                    SessionCookie: sessionCookie,
                    SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),
                    ApiKey: passkey);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Falls through to the same "sign in again" answer a rejected session gets.
        }

        return new AccountCheckResult(false, AccountType.Free, "The saved DepositFiles sign-in is no longer valid — sign in again.");
    }

    /// <summary>The API's own error envelope, or null when the reply is a success.</summary>
    private static (int Code, string Error)? ReadApiError(JsonElement root)
    {
        if (!root.TryGetProperty("status", out JsonElement status)
            || string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int code = root.TryGetProperty("error_code", out JsonElement c) && c.TryGetInt32(out int parsed) ? parsed : 0;
        string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "unknown error" : "unknown error";
        return (code, error);
    }

    private Task<HttpResponseSnapshot> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url, headers) : handler.GetSnapshotAsync(url, headers, ct);

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _postFormOverride is not null ? _postFormOverride(url, form, headers) : handler.PostFormAsync(url, form, headers, ct);

    /// <summary>Headers for the JSON API. <c>X-Requested-With</c> is what the site's own calls send.</summary>
    private static Dictionary<string, string> ApiHeaders(string? session)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        if (session is not null)
        {
            headers["Cookie"] = SessionCookieName + "=" + session;
        }

        return headers;
    }

    private static Dictionary<string, string> PageHeaders(string session) => new(StringComparer.Ordinal)
    {
        ["Referer"] = Host + "/",
        ["Cookie"] = SessionCookieName + "=" + session,
    };

    private static string? ToHttps(string? url)
        => url is null ? null : url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response)";
        }

        string trimmed = body.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
