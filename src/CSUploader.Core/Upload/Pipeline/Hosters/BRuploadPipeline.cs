// <copyright file="BRuploadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// BRupload upload pipeline. Unlike Rapidgator/Alfafile (clean JSON APIs with bearer
/// tokens), BRupload is a classic XFileSharing-style PHP frontend:
/// <list type="bullet">
///   <item>Login goes through an HTML form: GET <c>/login.html</c> to harvest a CSRF
///   token, then POST the form-urlencoded fields to <c>/</c>. The server replies with a
///   302 plus a <c>Set-Cookie: xfss=&lt;sess_id&gt;</c>.</item>
///   <item>After login, GET <c>/?op=upload_form</c> with the <c>xfss</c> cookie. The
///   returned HTML embeds the per-user upload subdomain in its <c>&lt;form action="…"&gt;</c>
///   attribute (real backend: e.g. <c>https://server54.brupload.net/cgi-bin/upload.cgi</c>;
///   <c>www.brupload.net</c> rejects large multipart bodies mid-stream). The form also
///   carries the <c>sess_id</c> we must echo back.</item>
///   <item>Upload is a single multipart POST to the scraped action URL with form fields
///   <c>sess_id, utype, file_descr, file_public, link_rcpt, link_pass, to_folder,
///   upload, keepalive</c> and a <c>file_0</c> file part. Response body is the upload
///   result: <c>[{file_code, file_status}]</c>.</item>
///   <item>The public URL is derived from <c>file_code</c>: <c>https://www.brupload.net/&lt;code&gt;</c>.</item>
/// </list>
/// No hashing, no folder, no post-upload polling.
/// </summary>
public sealed partial class BRuploadPipeline : IFileHosterPipeline
{
    private const string Host = "https://www.brupload.net";
    private const string LoginPageUrl = Host + "/login.html";
    private const string LoginPostUrl = Host + "/";
    private const string UploadFormUrl = Host + "/?op=upload_form";
    private const string MyFilesUrl = Host + "/?op=my_files";
    private const string PublicUrlPrefix = Host + "/";

    private readonly ConcurrentDictionary<int, BRuploadAuthState> _authByCredentialsId = new();

    // One login at a time per credentials id. Same rationale as the other pipelines:
    // kicking off N parallel uploads shouldn't fan out into N login round-trips.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, Task<string>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    // Matches `name="X" value="Y"` or `value="Y" name="X"` for a given input name — used
    // both for the CSRF `token` field on login.html and `sess_id` on upload_form.html.
    // XFileSharing templates render attribute order inconsistently across versions.
    private static readonly Regex _csrfTokenRegex = BuildHiddenInputRegex("token");
    private static readonly Regex _sessIdRegex = BuildHiddenInputRegex("sess_id");

    // Matches the <form ...action="...upload.cgi..."...> opening tag. Liberal on attribute
    // ordering and quoting so it works whether the upload form has id="uploadfile" first,
    // method="POST" first, etc. The `upload.cgi` substring keeps us from matching the
    // outer my-files form.
    private static readonly Regex _uploadFormActionRegex = MyRegex();

    // Matches BRupload's storage-quota line on the /?op=my_files page:
    //   "Espaço utilizado:\n<strong>0.74 de 100 GB</strong>"
    // (Portuguese template only — BRupload is Brazil-only.) Anchored on "utilizado:" so
    // an unrelated "<strong>X de Y GB</strong>" elsewhere on the page can't false-match.
    // Groups: (1) used number, (2) total number, (3) unit (KB/MB/GB/TB).
    // s-flag so the \s* between "utilizado:" and the strong tag spans newlines.
    private static readonly Regex _storageUsageRegex = new(
        @"utilizado\s*:\s*<strong>\s*([\d.,]+)\s+de\s+([\d.,]+)\s+([KMGTP]B)\s*</strong>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static Regex BuildHiddenInputRegex(string fieldName)
        => new(
            $"""name=["']{Regex.Escape(fieldName)}["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']{Regex.Escape(fieldName)}["']""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public BRuploadPipeline()
    {
    }

    internal BRuploadPipeline(
        Func<string, string> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getOverride = url => Task.FromResult(getOverride(url));
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _uploadOverride = uploadOverride;
    }

    /// <summary>Thrown internally when the server signals the session cookie is no longer valid.</summary>
    private sealed class AuthExpiredException : Exception { }

    public string Name => "BRupload";

    /// <summary>From its own plans table (read 2026-08-12, in Portuguese), "Arquivo expira em":
    /// guest "3 dias sem downloads", registered "30 dias sem downloads" (days without downloads),
    /// premium "NUNCA" (never).</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(3)
            : credentials.AccountType == AccountType.Premium ? FileRetention.Permanent
            : FileRetention.DaysAfterLastDownload(30);

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>1 GiB hard cap per file on the free tier (BRupload rejects oversized
    /// multipart bodies mid-stream).</summary>
    public long? MaxFileSize => 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// BRupload rate-limits to 30 files per upload session. Unlike the XFileSharing-API
    /// pipelines (which fetch a fresh <c>sess_id</c> per file via <c>/api/upload/server</c>
    /// and so don't hit any session cap), BRupload's auth flow caches one
    /// <c>(xfss, sess_id, actionUrl)</c> tuple per credentials in <c>_authByCredentialsId</c>
    /// and reuses it across every file in the package. So this cap genuinely binds at
    /// the protocol level today.
    /// </summary>
    /// <remarks>
    /// To lift the cap, the auth-cache design would need to change so each file gets a
    /// fresh <c>sess_id</c>: either re-fetch <c>/?op=upload_form</c> per file (cheap —
    /// one extra round-trip per file, xfss stays cached) or full re-login per file
    /// (expensive). Worth doing if real users start hitting the limit; until then the
    /// existing wizard truncation surfaces the cap clearly enough.
    /// </remarks>
    public int? MaxFilesPerPackage => 30;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Pre-check: hard size limit ===
        // BRupload's free tier silently closes the TCP connection mid-stream once the
        // multipart body crosses 1 GiB, so the upload would otherwise burn bandwidth then
        // surface as "Error while copying content to a stream" instead of a clear cap
        // message. Fail fast with the actual reason. The wizard already blocks oversized
        // files at queue time; this is the safety net for packages added by other paths.
        if (MaxFileSize is long maxBytes && ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds BRupload's {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()})",
                null);
            yield break;
        }

        // === Auth ===
        BRuploadAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out BRuploadAuthState? cached))
        {
            auth = cached;
        }
        else
        {
            (BRuploadAuthState? gated, bool didLogin, string? error) = await EnsureAuthAsync(ctx, ct);

            if (didLogin)
            {
                yield return new AuthStarted();
            }

            if (gated is null)
            {
                if (didLogin)
                {
                    yield return new AuthFailed(error ?? "login failed");
                }
                yield return new AttemptFailed(error ?? "login failed", null);
                yield break;
            }

            if (didLogin)
            {
                yield return new AuthSucceeded();
            }

            auth = gated;
        }

        // === Upload ===
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        yield return new TransferStarted(ctx.FileSize);

        // Bridge UploadProgress -> TransferProgress events via an unbounded channel,
        // same pattern as the other pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
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
            (string? Url, string? Error, bool AuthExpired) = ParseUploadResponse(uploadResponse);
            if (AuthExpired)
            {
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                authExpired = true;
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

    private async Task<(BRuploadAuthState? Auth, bool DidLogin, string? Error)> EnsureAuthAsync(AttemptContext ctx, CancellationToken ct)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out BRuploadAuthState? cached))
            {
                return (cached, false, null);
            }

            // Step 1: GET login.html + POST login → xfss cookie.
            (string? xfss, string? loginError) = await PerformLoginAsync(
                ctx.Credentials.Username,
                ctx.Credentials.Password,
                url => GetAsync(ctx, url),
                (url, form) => PostFormAsync(ctx, url, form));

            if (xfss is null)
            {
                return (null, true, loginError);
            }

            // Step 2: GET /?op=upload_form with the xfss cookie to discover the per-user
            // upload server hostname (XFileSharing routes uploads through a sharded
            // subdomain that the main www host won't accept).
            (string? sessionId, string? actionUrl, string? formError, bool sessIdFromForm, int htmlLength) =
                await FetchUploadFormAsync(
                    xfss,
                    url => GetAsync(ctx, url, BuildCookieHeader(xfss)));

            if (sessionId is null || actionUrl is null)
            {
                return (null, true, formError ?? "upload_form parse failed");
            }

            // One-shot diagnostic: confirms on a live trace whether _sessIdRegex actually
            // picks up the hidden input the real backend renders, or whether we silently
            // fell back to using xfss as sess_id (which fs.cgi may reject). Logged at
            // Status so it shows up in the Logs tab without needing a debug build.
            ctx.Logger.Log(
                this,
                LogType.Status,
                $"BRupload upload_form: action={actionUrl}, sess_id source={(sessIdFromForm ? "form" : "xfss-fallback")}, " +
                $"sess_id={sessionId}, xfss={xfss}, html_length={htmlLength}");

            BRuploadAuthState newAuth = new(xfss, sessionId, actionUrl);
            _authByCredentialsId[ctx.Credentials.Id] = newAuth;
            return (newAuth, true, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // BRupload's HTML-form login doesn't need the proxy choice separately — the handler already routes through it.
        _ = apiKey; // BRupload doesn't support API keys.
        // Account verification only needs the login round-trip — we deliberately skip the
        // upload_form fetch since CheckAccount doesn't care about the upload subdomain
        // and the form GET adds a round-trip the Settings UI doesn't need.
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> get =
            _getOverride is not null
                ? (url, _) => _getOverride(url)
                : (url, headers) => handler.GetStringAsync(url, headers, ct);
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postForm =
            _postFormOverride ?? ((url, form) => handler.PostFormAsync(url, form, ct));

        string? xfss;
        string? error;
        try
        {
            (xfss, error) = await PerformLoginAsync(username, password, url => get(url, null), postForm);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (xfss is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "login failed");
        }

        // Fetch storage usage opportunistically. Failure here is non-fatal — the Accounts
        // grid's Used/Available columns just stay blank for that refresh. Reuses the same
        // get-override the login flow used so tests can stub by URL.
        (long? storageUsed, long? storageTotal) = await TryFetchStorageStatsAsync(
            xfss,
            url => get(url, BuildCookieHeader(xfss)));

        // BRupload doesn't expose premium state in the login response (it lives on the
        // /?op=my_account HTML page). Verifying the login is the most we can confirm here.
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Login OK",
            StorageUsedBytes: storageUsed,
            StorageQuotaBytes: storageTotal);
    }


    /// <summary>
    /// Calls <c>GET /?op=my_files</c> with the <c>xfss</c> cookie and scrapes the
    /// storage-usage line ("Espaço utilizado: &lt;strong&gt;X de Y GB&lt;/strong&gt;") out
    /// of the returned HTML. Returns (null, null) on any failure — caller treats that as
    /// "unknown", not a hard error. Internal so tests can drive it with canned HTML.
    /// </summary>
    internal static async Task<(long? Used, long? Total)> TryFetchStorageStatsAsync(
        string xfss,
        Func<string, Task<string>> get)
    {
        _ = xfss; // future use — currently the get override carries the cookie header

        try
        {
            string html = await get(MyFilesUrl).ConfigureAwait(false);
            return TryParseStorageFromHtml(html);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Pure parser for the BRupload my_files storage line. The page renders
    /// <c>&lt;strong&gt;0.74 de 100 GB&lt;/strong&gt;</c> as the only "X de Y UNIT"
    /// occurrence (anchored on the preceding "utilizado:" word in the regex). Returns
    /// decimal-base bytes — BRupload's "100 GB" is treated as 100 × 10^9, not 100 × 2^30,
    /// because consumer file hosters advertise capacity in decimal GB and the user
    /// expects their pricing-page number to match what shows in the grid.
    /// </summary>
    internal static (long? Used, long? Total) TryParseStorageFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return (null, null);
        }

        Match m = _storageUsageRegex.Match(html);
        if (!m.Success)
        {
            return (null, null);
        }

        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double used)
            || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double total))
        {
            return (null, null);
        }

        long multiplier = m.Groups[3].Value.ToUpperInvariant() switch
        {
            "KB" => 1_000L,
            "MB" => 1_000_000L,
            "GB" => 1_000_000_000L,
            "TB" => 1_000_000_000_000L,
            "PB" => 1_000_000_000_000_000L,
            _ => 0L,
        };

        if (multiplier == 0)
        {
            return (null, null);
        }

        return ((long)(used * multiplier), (long)(total * multiplier));
    }

    /// <summary>
    /// Steps 1+2 of auth: fetch login.html for the CSRF token, then POST the credential
    /// form. Returns the <c>xfss</c> session cookie value on success or an error string
    /// on failure. Used by both <see cref="RunAsync"/> and <see cref="CheckAccountAsync"/>.
    /// </summary>
    private static async Task<(string? Xfss, string? Error)> PerformLoginAsync(
        string? username,
        string? password,
        Func<string, Task<string>> get,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postForm)
    {
        string loginPage = await get(LoginPageUrl);
        string? csrf = ExtractHiddenInput(_csrfTokenRegex, loginPage);
        if (csrf is null)
        {
            // The token IS present on the real login.html (verified against the live page
            // serving `<input type="hidden" name="token" value="...">`). When this branch
            // fires it means we got DIFFERENT HTML than the real page — typically the
            // mock-server URL rewrite (UseMockServer in Settings → General, off by default)
            // intercepting and returning a stub page. Surface enough context that the
            // user can tell from the error which case they're in.
            string snippet = Snippet(loginPage);
            string lengthInfo = $"body length: {loginPage.Length}";
            return (null, $"login.html did not contain a CSRF token ({lengthInfo}). " +
                $"If you have the mock server enabled in Settings → General, disable it. " +
                $"Body starts with: {snippet}");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "login",
            ["token"] = csrf,
            ["login"] = username ?? string.Empty,
            ["password"] = password ?? string.Empty,
            ["rand"] = string.Empty,
            ["redirect"] = string.Empty,
        };
        HttpResponseSnapshot response = await postForm(LoginPostUrl, form);

        string? xfss = ExtractCookieValue(response.SetCookies, "xfss");
        if (xfss is null)
        {
            return (null, FormatLoginError(response));
        }

        return (xfss, null);
    }

    /// <summary>
    /// Step 3 of auth: fetch the upload-form HTML (authenticated via the <c>xfss</c>
    /// cookie) and parse out the upload action URL and the <c>sess_id</c> hidden field.
    /// Returns <paramref name="SessIdFromForm"/> = false when the regex didn't match and
    /// we fell back to the xfss cookie value — surfaced for diagnostic logging.
    /// </summary>
    private static async Task<(string? SessionId, string? ActionUrl, string? Error, bool SessIdFromForm, int HtmlLength)> FetchUploadFormAsync(
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
            return (null, null, "upload_form fetch failed: " + ex.Message, false, 0);
        }

        Match actionMatch = _uploadFormActionRegex.Match(html);
        if (!actionMatch.Success)
        {
            return (null, null, "upload_form HTML did not contain a usable upload action URL", false, html.Length);
        }

        string actionUrl = actionMatch.Groups[1].Value;

        // sess_id can equal xfss on the mock, but on the real backend the form may render
        // a different value (e.g. CSRF-bound or short-lived). Always use the form value.
        string? sessId = ExtractHiddenInput(_sessIdRegex, html);
        bool fromForm = !string.IsNullOrEmpty(sessId);
        if (!fromForm)
        {
            // Fall back to the cookie value — the mock form does use it as sess_id, so this
            // keeps test fixtures that omit the sess_id input working.
            sessId = xfss;
        }

        return (sessId, actionUrl, null, fromForm, html.Length);
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, BRuploadAuthState auth)
    {
        // Mirror exactly what BRupload's upload form posts via the browser. A previous
        // iteration omitted file_descr / file_public / upload on the theory they belonged
        // to the URL-upload form, but a captured Fiddler trace of a successful browser
        // upload shows all three are present and that fs.cgi 500s ("failed while
        // requesting fs.cgi") when they're missing — fs.cgi reads file_public to register
        // the file's visibility and chokes on the unset value.
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

        // Stash for both code paths (override + real).
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

        // The header set (uploadHeaders, built above) the browser sends on this exact
        // request, per a captured Fiddler trace:
        //   - Origin: https://www.brupload.net — XFileSharing's upload.cgi reads this to
        //     decide whether the request originated from its own web UI. Without it the
        //     request looks like an external API call and the server replies with the
        //     opaque "uploads are not enabled for your account type" (NOT a real account
        //     issue — the same account uploads fine through the browser).
        //   - Sec-Fetch-Site / -Mode / -Dest — Chrome client hints. Cheap to send, brings
        //     the request closer to the browser's fingerprint for any WAF that scores on them.
        // Deliberately NOT sent (matches the browser):
        //   - Cookie: xfss=… — scoped to www.brupload.net, not the upload subdomain.
        //   - Referer — Chrome's cross-origin policy strips it here.
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

    /// <summary>
    /// Parses the <c>cgi-bin/upload.cgi</c> JSON response. The body is an array
    /// (one entry per uploaded file) of <c>{file_code, file_status}</c>. We only
    /// upload one file at a time, so we look at the first entry.
    /// </summary>
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

    /// <summary>
    /// Extracts the <c>value</c> attribute of a hidden input identified by name. Picks
    /// whichever capture group matched the active attribute order. Returns null when
    /// the input isn't present.
    /// </summary>
    private static string? ExtractHiddenInput(Regex regex, string html)
    {
        Match m = regex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string captured = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return string.IsNullOrEmpty(captured) ? null : captured;
    }

    /// <summary>
    /// Finds a cookie by name in a list of raw <c>Set-Cookie</c> header values and returns
    /// its value (the bit between <c>name=</c> and the next <c>;</c>). Returns null when
    /// the cookie isn't present or its value is empty.
    /// </summary>
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

    private static string FormatLoginError(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 400)
        {
            return $"login failed (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        // 2xx/3xx without the cookie means the credentials were rejected (form re-rendered
        // with an error banner) — the body is HTML, snip it for the log/UI.
        return $"login failed — no session cookie returned: {Snippet(response.Body)}";
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

    private Task<string> GetAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string>? headers = null)
        => _getOverride is not null
            ? _getOverride(url)
            : ctx.Handler.GetStringAsync(url, headers, ctx.Cancellation);

    private Task<HttpResponseSnapshot> PostFormAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string> form)
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : ctx.Handler.PostFormAsync(url, form, ctx.Cancellation);

    private sealed class UploadResult
    {
        [JsonPropertyName("file_code")] public string? Code { get; set; }

        [JsonPropertyName("file_status")] public string? Status { get; set; }
    }

    [GeneratedRegex("""<form\b[^>]*?\baction=["']([^"']*upload\.cgi[^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
