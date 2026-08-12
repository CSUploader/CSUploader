// <copyright file="UploadEePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// upload.ee — anonymous, and the first host in the tree running <b>Uber-Uploader</b>, a Perl CGI
/// uploader that is neither XFileSharing nor a drop host. Three steps, from a browser capture
/// 2026-08-05 and verified end to end with real bytes:
/// <list type="number">
///   <item><b>Get an id.</b> <c>GET /ubr_link_upload.php?rnd_id=&lt;epoch-ms&gt;</c> answers a line of
///   JavaScript — <c>if(typeof startUpload==='function'){startUpload("&lt;32 hex&gt;",0);}</c> — and the
///   id inside it is <b>minted by the server</b>.</item>
///   <item><b>Upload.</b> Multipart POST to
///   <c>/cgi-bin/ubr_upload.pl?X-Progress-ID=&lt;id&gt;&amp;upload_id=&lt;id&gt;</c> carrying
///   <b>only</b> <c>upfile_0</c> — no other fields at all.</item>
///   <item><b>Collect the link.</b> The reply points at
///   <c>/?page=finished&amp;upload_id=&lt;id&gt;</c>, which renders "View file" (the share link) and a
///   <c>?killcode=</c> delete link.</item>
/// </list>
/// <para>
/// <b>Step 1 is not optional and cannot be faked.</b> Posting a self-invented id — the obvious first
/// guess — reaches the script and dies inside it with
/// <c>Ei suutnud avada link faili …/&lt;id&gt;.link</c> ("could not open link file") at
/// <c>ubr_upload.pl</c> line 124: the server writes that file when it hands the id out, so an id it
/// never issued has nothing behind it. That failure is what the capture resolved.
/// </para>
/// <para>
/// <b>⚠ The finish step answers differently for us than for a browser.</b> The capture shows a
/// <c>302</c> with a <c>Location</c>; this client gets <c>200</c> whose body is
/// <c>parent.location.href='…?page=finished&amp;upload_id=…'</c> — the iframe-era redirect. Both are
/// handled, because relying on either alone would work in testing and fail in the field.
/// </para>
/// <para>
/// Anonymous uploads are capped at <b>100 MB</b> and kept until <b>50 days after the last download</b>;
/// an account doubles the first to <b>200 MB</b> and the second to <b>120 days</b>. Anonymous needs no
/// cookie at all — the flow issues no session, and the only <c>Set-Cookie</c> in an anonymous capture
/// is a language preference.
/// </para>
/// <para>
/// <b>An account changes nothing about the three steps</b> — it only puts a session cookie on them.
/// Signing in is a plain form with no captcha (from a capture 2026-08-06):
/// <list type="number">
///   <item><c>GET /</c> for the login form's <c>___nonce</c>;</item>
///   <item><c>POST /login.html</c> with <c>u[username]</c>, <c>u[password]</c>, empty <c>u[page]</c>,
///   that nonce and <c>login=" Enter "</c> → <b>302</b> setting <c>upload_sess_sec</c>;</item>
///   <item>follow the redirect, which is where <c>sess_sec</c> is set — the upload steps send both,
///   so stopping at the 302 yields a half-session.</item>
/// </list>
/// </para>
/// <para>
/// <b>⚠ It inspects inside archives</b>, which no other host here does: per its FAQ a single unpacked
/// file may not exceed 200 MB, the total extracted may not exceed 400 MB, and an archive over 50 MB
/// whose contents expand more than fivefold is refused. Ordinary release parts pass; this cannot be
/// checked locally, so it surfaces as the host's own refusal.
/// </para>
/// </summary>
public sealed class UploadEePipeline : IFileHosterPipeline
{
    private const string Host = "https://www.upload.ee";
    private const string LinkUploadUrl = Host + "/ubr_link_upload.php";
    private const string UploadScriptUrl = Host + "/cgi-bin/ubr_upload.pl";

    /// <summary>Anonymous cap, from its FAQ ("Unregistered users can upload up to 100MB files").
    /// Binary, matching how the other 100 MB host in the tree (tmpfiles.org) reads its own figure.</summary>
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>"registered users can upload up to 200MB files", same FAQ.</summary>
    private const long RegisteredMaxFileSizeBytes = 200L * 1024 * 1024;

    private const string LoginUrl = Host + "/login.html";

    // if(typeof startUpload==='function'){startUpload("c93cc90ca1aeac83b3586aad022b9b62",0);}
    private static readonly Regex _uploadIdRegex = new(
        """startUpload\(\s*["']([0-9a-fA-F]{8,})["']""",
        RegexOptions.Compiled);

    // The iframe-era redirect this client receives in place of the browser's 302.
    private static readonly Regex _jsRedirectRegex = new(
        """(?:parent\.)?location\.href\s*=\s*["']([^"']*page=finished[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <h1>File successfully uploaded!!</h1> … View file:<br /><a href="…/files/<id>/<name>.html">
    private static readonly Regex _viewLinkRegex = new(
        """View\s+file:.{0,120}?href=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex _killcodeRegex = new(
        """href=["']([^"']+killcode=[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <input type="hidden" name="___nonce" value="71070709_bc90ab…" /> on the login form.
    private static readonly Regex _nonceRegex = new(
        """name=["']___nonce["'][^>]*?\bvalue=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The header's greeting, which names the account.
    // ⚠ On the wire it is "Welcome, <b>name</b>!" — the name is WRAPPED IN MARKUP. The first version of
    // this pattern was written against a capture-analysis script's TAG-STRIPPED text ("Welcome, name !"),
    // a shape that never crosses the wire, so it matched nothing and every real sign-in "failed".
    private static readonly Regex _welcomeRegex = new(
        """Welcome,\s*(?:<[^>]*>|\s)*([^<>!\r\n]+?)\s*(?:</[^>]*>|\s)*!""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The logout control the header renders once there is a session. A second, independent marker:
    // across the whole capture it is on every signed-in page and on none of the anonymous ones, so a
    // cosmetic change to the greeting can no longer read as a failed login.
    private static readonly Regex _signedInRegex = new(
        """logout\.html|name=["']logout["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The session cookie pair. upload_sess_sec arrives on the login's 302; sess_sec only on the page
    // the redirect points at, and the upload steps send both.
    private static readonly string[] SessionCookieNames = ["upload_sess_sec", "sess_sec", "lng"];

    // One session per credentials id, and one login at a time for it — a batch of N files against the
    // same account signs in ONCE. Same shape as filehoster.io's xfss cache and catbox's userhash.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _cookiesByCredId = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postFormOverride;

    public UploadEePipeline()
    {
    }

    /// <summary>Test ctor — stubs the GETs and the multipart POST so the three-step orchestration runs
    /// without the network. The form POST is optional: only the account path uses it.</summary>
    internal UploadEePipeline(
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? postFormOverride = null)
    {
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
        _postFormOverride = postFormOverride;
    }

    public string Name => "Upload.ee";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>The anonymous figure — the conservative one, and what the wizard shows before an
    /// account is picked. See <see cref="MaxFileSizeFor"/>.</summary>
    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>50 days after the last download anonymously, 120 signed in - the second half of what an
    /// account buys here, the first being 200 MB instead of 100.</summary>
    public FileRetention RetentionFor(FileHosterLoginDto credentials)
        => FileRetention.DaysAfterLastDownload(credentials.IsAnonymous ? 50 : 120);

    /// <summary>
    /// 200 MB signed in, 100 MB anonymous — its FAQ states both, and the account tier is the only
    /// reason to sign in to this host at all (that, and 120 days of retention instead of 50).
    /// </summary>
    public long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? MaxFileSizeBytes : RegisteredMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous needs no credential at all; an account merely raises the limits.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        long cap = MaxFileSizeFor(ctx.Credentials) ?? MaxFileSizeBytes;
        if (ctx.FileSize > cap)
        {
            yield return new AttemptFailed(
                $"File exceeds upload.ee's {ByteUnit.FromBytes(cap, ByteBase.Binary).ToFriendlyString()} "
                + $"{(ctx.Credentials.IsAnonymous ? "anonymous" : "per-account")} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0 (account only): the session cookies the later steps carry ===
        string? cookies = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            (string? signedIn, string? loginError) = await EnsureSessionAsync(ctx);
            if (signedIn is null)
            {
                yield return new AttemptFailed(loginError ?? "upload.ee login failed", null);
                yield break;
            }

            cookies = signedIn;
        }

        // === Step 1: the server mints the upload id ===
        string rndId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        HttpResponseSnapshot? idResponse = null;
        string? idRequestError = null;
        try
        {
            idResponse = await GetAsync(ctx, $"{LinkUploadUrl}?rnd_id={rndId}", cookies);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // C# forbids yielding from a catch, so the failure is carried out of it.
            idRequestError = "upload.ee upload-id request failed: " + ex.Message;
        }

        if (idResponse is null)
        {
            yield return new AttemptFailed(idRequestError ?? "upload.ee upload-id request failed", null);
            yield break;
        }

        (string? uploadId, string? idError) = ParseUploadId(idResponse);
        if (uploadId is null)
        {
            yield return new AttemptFailed(idError!, null);
            yield break;
        }

        // === Step 2: the bytes ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadId, cookies);
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

        // A transport fault propagates to the shared retry layer as a safe-to-retry
        // UploadBodyTransferException; re-running mints a fresh id, so nothing is double-created.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        // === Step 3: the result page carries the link ===
        // How that page arrives depends on the CLIENT'S REDIRECT POLICY, which is why all three shapes
        // are handled rather than the one a given run happens to show:
        //   • our HttpHandler follows the 302, so the upload's own body IS the finished page;
        //   • a client that doesn't follow gets 302 + Location (the browser capture);
        //   • or 200 whose body is parent.location.href='…' (the iframe-era redirect).
        // Reading the body we already hold first also saves a request in the common case.
        (string? url, string? deleteLink, string? _) = ParseFinishedPage(uploadResponse);

        if (url is null)
        {
            (string? finishedUrl, string? uploadError) = ParseFinishedUrl(uploadResponse, uploadId);
            if (finishedUrl is null)
            {
                yield return new AttemptFailed(uploadError!, null);
                yield break;
            }

            HttpResponseSnapshot? finished = null;
            string? finishRequestError = null;
            try
            {
                finished = await GetAsync(ctx, finishedUrl, cookies);
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                finishRequestError = "upload.ee result page fetch failed: " + ex.Message;
            }

            if (finished is null)
            {
                // The bytes are already on their server by now; only the link is missing. Say that,
                // rather than implying the upload itself failed.
                yield return new AttemptFailed(
                    (finishRequestError ?? "upload.ee result page fetch failed")
                        + $" — the file may well have uploaded; its page is {finishedUrl}",
                    null);
                yield break;
            }

            string? finishError;
            (url, deleteLink, finishError) = ParseFinishedPage(finished);
            if (url is null)
            {
                yield return new AttemptFailed(finishError!, null);
                yield break;
            }
        }

        // The killcode appears once, on this page, and an anonymous upload has no account to manage the
        // file from — so log it rather than drop it, as Sendspace and FILEAXA do.
        if (deleteLink is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: delete link for {ctx.FileName} — {deleteLink}");
        }

        int days = ctx.Credentials.IsAnonymous ? 50 : 120;
        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} is kept until {days.ToString(CultureInfo.InvariantCulture)} days after its last download.");
        yield return new TransferCompleted(url);
    }

    /// <summary>
    /// Validates an account by actually signing in — there is no API to ask instead. The account's own
    /// name comes back from the "Welcome, …" header rather than the typed value, so a login that
    /// succeeds against a different case or an e-mail alias still shows the real name.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? cookies, string? derivedName, string? error) = await LoginAsync(
            (url, headers) => GetSnapshotAsync(handler, url, headers, ct),
            (url, form, headers) => PostFormAsync(handler, url, form, headers, ct),
            username,
            password);

        return cookies is null
            ? new AccountCheckResult(false, AccountType.Free, error ?? "upload.ee login failed.")
            : new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in (Free) — 200 MB per file, kept 120 days after last download",
                DerivedUsername: derivedName ?? (string.IsNullOrWhiteSpace(username) ? null : username));
    }

    /// <summary>Returns the cached session for the account, signing in once (gated per credentials id)
    /// on a miss — a batch of N files does ONE login, not N.</summary>
    private async Task<(string? Cookies, string? Error)> EnsureSessionAsync(AttemptContext ctx)
    {
        int id = ctx.Credentials.Id;
        if (_cookiesByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_cookiesByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? cookies, string? _, string? error) = await LoginAsync(
                (url, headers) => GetSnapshotAsync(ctx.Handler, url, headers, ctx.Cancellation),
                (url, form, headers) => PostFormAsync(ctx.Handler, url, form, headers, ctx.Cancellation),
                ctx.Credentials.Username,
                ctx.Credentials.Password);

            if (cookies is null)
            {
                return (null, error);
            }

            _cookiesByCredId[id] = cookies;
            return (cookies, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The three-request sign-in, written against the capture. Returns a ready-to-send
    /// <c>Cookie</c> header and the account name the site greets you by.
    /// <para>
    /// <b>The third request is the one that is easy to skip.</b> The login's 302 sets only
    /// <c>upload_sess_sec</c>; <c>sess_sec</c> is set by the page it redirects to, and the upload steps
    /// send both — so stopping at the redirect leaves a half-session that looks signed in and isn't.
    /// </para>
    /// </summary>
    private static async Task<(string? Cookies, string? DerivedName, string? Error)> LoginAsync(
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> get,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> postForm,
        string? username,
        string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, null, "upload.ee needs a username and password.");
        }

        Dictionary<string, string> jar = new(StringComparer.OrdinalIgnoreCase);

        // 1 — the login form's anti-CSRF nonce (and the language cookie that rides along).
        HttpResponseSnapshot home;
        try
        {
            home = await get(Host + "/", BrowserHeaders(null));
        }
        catch (Exception ex)
        {
            return (null, null, "upload.ee login page fetch failed: " + ex.Message);
        }

        Collect(jar, home);
        Match nonce = _nonceRegex.Match(home.Body);
        if (!nonce.Success)
        {
            return (null, null, $"upload.ee login form carried no ___nonce: {Snippet(home.Body)}");
        }

        // 2 — the sign-in itself.
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["u[username]"] = username,
            ["u[password]"] = password,
            ["u[page]"] = string.Empty,
            ["___nonce"] = nonce.Groups[1].Value,
            ["login"] = " Enter ",   // the submit button's value, verbatim — spaces included
        };

        HttpResponseSnapshot login;
        try
        {
            login = await postForm(LoginUrl, form, BrowserHeaders(CookieHeader(jar)));
        }
        catch (Exception ex)
        {
            return (null, null, "upload.ee login request failed: " + ex.Message);
        }

        Collect(jar, login);

        // 3 — follow the redirect, which is where sess_sec is issued.
        string next = login.LocationHeader is { Length: > 0 } loc ? Absolute(loc) : Host + "/?";
        HttpResponseSnapshot landed;
        try
        {
            landed = await get(next, BrowserHeaders(CookieHeader(jar)));
        }
        catch (Exception ex)
        {
            return (null, null, "upload.ee post-login page fetch failed: " + ex.Message);
        }

        Collect(jar, landed);

        // Either marker means signed in: the greeting names the account, the logout control proves the
        // session. The site re-renders the same page for a bad password rather than saying so, so their
        // absence is the only failure signal there is.
        Match welcome = _welcomeRegex.Match(landed.Body);
        if (!welcome.Success && !_signedInRegex.IsMatch(landed.Body))
        {
            return (null, null, "upload.ee login failed — check the username and password.");
        }

        return (CookieHeader(jar), welcome.Success ? welcome.Groups[1].Value.Trim() : null, null);
    }

    /// <summary>Adds a response's <c>Set-Cookie</c> values to the jar, keeping only the names
    /// upload.ee's own requests carry.</summary>
    private static void Collect(Dictionary<string, string> jar, HttpResponseSnapshot response)
    {
        foreach (string raw in response.SetCookies)
        {
            int eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = raw[..eq].Trim();
            if (!SessionCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            int semi = raw.IndexOf(';', eq);
            string value = (semi < 0 ? raw[(eq + 1)..] : raw[(eq + 1)..semi]).Trim();
            if (value.Length > 0)
            {
                jar[name] = value;
            }
        }
    }

    private static string? CookieHeader(Dictionary<string, string> jar)
        => jar.Count == 0 ? null : string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>Reads the id out of the JavaScript step 1 answers with. Internal for testing.</summary>
    internal static (string? UploadId, string? Error) ParseUploadId(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"upload.ee wouldn't issue an upload id (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        Match m = _uploadIdRegex.Match(response.Body);
        return m.Success
            ? (m.Groups[1].Value, null)
            : (null, $"upload.ee returned no upload id: {Snippet(response.Body)}");
    }

    /// <summary>
    /// Where to collect the link, from either shape the host answers with: a <c>302</c>'s
    /// <c>Location</c> (what a browser gets) or the <c>parent.location.href='…'</c> in a <c>200</c>
    /// body (what this client gets). Falls back to composing the URL from the id, since we know it.
    /// Internal for testing.
    /// </summary>
    internal static (string? FinishedUrl, string? Error) ParseFinishedUrl(HttpResponseSnapshot response, string uploadId)
    {
        if (response.LocationHeader is { Length: > 0 } location)
        {
            return (Absolute(location), null);
        }

        Match m = _jsRedirectRegex.Match(response.Body);
        if (m.Success)
        {
            return (Absolute(m.Groups[1].Value), null);
        }

        if (response.StatusCode is < 200 or >= 400)
        {
            return (null, $"upload.ee rejected the upload (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        // Both redirect shapes absent but the request was accepted: the id is ours, so the result page
        // is addressable anyway. Better than failing an upload that may well have landed.
        return ($"{Host}/?page=finished&upload_id={uploadId}", null);
    }

    /// <summary>Pulls the share link and the killcode delete link off the result page. Internal for
    /// testing.</summary>
    internal static (string? Url, string? DeleteLink, string? Error) ParseFinishedPage(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"upload.ee result page failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        Match view = _viewLinkRegex.Match(response.Body);
        if (!view.Success)
        {
            return (null, null, $"upload.ee result page carried no file link: {Snippet(response.Body)}");
        }

        Match kill = _killcodeRegex.Match(response.Body);
        return (Absolute(view.Groups[1].Value), kill.Success ? Absolute(kill.Groups[1].Value) : null, null);
    }

    private static string Absolute(string url)
        => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : Host + (url.StartsWith('/') ? url : "/" + url);

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

    private Task<HttpResponseSnapshot> GetAsync(AttemptContext ctx, string url, string? cookies)
        => GetSnapshotAsync(ctx.Handler, url, BrowserHeaders(cookies), ctx.Cancellation);

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null
            ? _getOverride(url, headers)
            : handler.GetSnapshotAsync(url, headers, ct);

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _postFormOverride is not null
            ? _postFormOverride(url, form, headers)
            : handler.PostFormAsync(url, form, headers, ct);

    /// <summary>What every request carries, plus the session when there is one. Anonymous uploads send
    /// no cookie at all — the flow issues none.</summary>
    private static Dictionary<string, string> BrowserHeaders(string? cookies)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Referer"] = Host + "/",
            ["Origin"] = Host,
        };

        if (!string.IsNullOrEmpty(cookies))
        {
            headers["Cookie"] = cookies;
        }

        return headers;
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadId, string? cookies)
    {
        // Both query parameters carry the same id: X-Progress-ID is what the progress endpoint polls
        // on, upload_id is what the script itself keys on. The capture sends both; so do we.
        string url = $"{UploadScriptUrl}?X-Progress-ID={uploadId}&upload_id={uploadId}";

        // The capture's POST carries the file and nothing else — no category, no token.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal);

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, url, extraFields, BrowserHeaders(cookies), ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            url,
            fileFieldName: "upfile_0",
            extraFields: extraFields,
            headers: BrowserHeaders(cookies),
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }
}
