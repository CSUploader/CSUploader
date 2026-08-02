// <copyright file="FilestankPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Filestank (filestank.com) — account upload. It is NOT XFileSharing: it runs <b>YetiShare</b>
/// (<c>/api/v2/</c>, <c>themes/spirit</c>), a different commercial script whose uploader is
/// blueimp jQuery-File-Upload against a separate storage node. Built from a browser capture of a
/// signed-in upload, 2026-08-01.
/// <list type="number">
///   <item><b>Sign in.</b> WebView at <c>/account/login</c> (reCAPTCHA), capturing the
///   <c>filehosting</c> session cookie. That cookie exists BEFORE login and keeps the same value
///   after it, so completion waits for the browser to leave the login page rather than for the
///   cookie to appear.</item>
///   <item><b>Scrape the upload ticket.</b> <c>GET /assets/js/uploader.js</c> is generated per
///   session and carries all three moving parts: the node URL
///   (<c>strN.filestank.com/ajax/file_upload_handler?…csaKey1=…&amp;csaKey2=…</c>), the
///   <c>_sessionid</c>, and a <c>cTracker</c>. All of them rotate, so this runs per upload.</item>
///   <item><b>Upload.</b> Multipart POST to that node: <c>_sessionid</c>, <c>cTracker</c>,
///   <c>maxChunkSize</c>, <c>folderId</c>, <c>uploadSource</c> and the file under <c>files[]</c> →
///   a JSON array <c>[{…,"url":…,"error":null}]</c>. The share link is that <c>url</c>.</item>
/// </list>
/// <para>
/// <b>The storage node is authenticated by a form field, not a cookie.</b> The browser sends no
/// session cookie to <c>strN.</c> at all — <c>_sessionid</c> in the body is the whole credential
/// there. Sending the cookie instead would upload as a guest (or be refused outright), so the
/// scrape is not optional plumbing: it IS the authentication.
/// </para>
/// <para>
/// <b>Files over 100 MB are chunked</b>, because the site's own uploader chunks them: its
/// jQuery-File-Upload options set <c>maxChunkSize: 100000000</c>, which makes the widget split the
/// file into 100 MB parts, each POSTed to the same URL with a <c>Content-Range</c> header (the
/// node's CORS pre-flight allows exactly that header). A live 100 MB chunk was accepted and
/// answered normally by the node on 2026-08-01; what is still unconfirmed is a full multi-chunk
/// file assembling into one link, which needs a run with allowance left.
/// </para>
/// <para>
/// <b>Per-file cap is 20 GiB</b> — <c>maxFileSize: 21474836480</c> in that same options block, which
/// the uploader also renders as "Max file size: 20.00 GB". A candidate note claimed 20 GB without a
/// source; this is that number, read from the account's own uploader. It is read per upload rather
/// than hard-coded, because it is a per-SESSION figure: 1 GiB for an anonymous trial account and
/// <b>0 for a signed-out session</b>, which is served a complete, valid-looking ticket regardless.
/// </para>
/// <para>
/// <b>There is also a daily upload COUNT limit</b> (about ten files on a free account, observed
/// 2026-08-01). It is invisible until spent, and then the node answers
/// <c>{"name":"Max uploads reached.","error":"You have reached the maximum permitted uploads for
/// today."}</c> — inside an HTTP 200, like every other refusal, and only after a chunk has been
/// pushed. That is the one refusal that says something about the ACCOUNT rather than the file, so
/// it is recognised and remembered per account for the rest of the day: the remaining files in a
/// batch fail instantly instead of each paying 100 MB to learn the same thing.
/// </para>
/// <para>
/// <b>Why sign-in and not the REST API.</b> Filestank publishes a full <c>/api/v2/</c> (authorize
/// with two 64-character keys → <c>access_token</c> → <c>file/upload</c>) and this pipeline was
/// first built on it. The account area exposes no API page and no way to obtain those keys, and
/// even if it did, "go find two 64-character keys" is a worse first-run credential than signing in
/// — the same reasoning that moved DDownload off its (working) API. If the keys ever become
/// reachable, the API is the more durable path: it has no cookie to expire.
/// </para>
/// </summary>
public sealed class FilestankPipeline : IFileHosterPipeline
{
    private const string SiteBase = "https://www.filestank.com";
    private const string LoginUrl = SiteBase + "/account/login";
    private const string AccountUrl = SiteBase + "/account";
    private const string StatsUrl = SiteBase + "/account/ajax/get_account_file_stats";

    /// <summary>Server-generated per session — the only place the node URL, <c>_sessionid</c> and
    /// <c>cTracker</c> appear together.</summary>
    private const string UploaderScriptUrl = SiteBase + "/assets/js/uploader.js";

    private const string CookieName = "filehosting";
    private const string CookieDomain = "www.filestank.com";

    /// <summary>blueimp's field name, as sent by the site.</summary>
    private const string FileFieldName = "files[]";

    /// <summary>The uploader's own <c>maxChunkSize</c>. Files at or below this go in one POST.</summary>
    private const long MaxChunkSize = 100_000_000;

    /// <summary>The uploader's own <c>maxFileSize</c>: 20 GiB.</summary>
    private const long UploaderMaxSize = 21_474_836_480;

    /// <summary>The node's <c>Set-Cookie</c> gives the session 24 h; expire ours earlier so an
    /// upload never starts against a session about to lapse mid-transfer.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(18);

    private static readonly Regex UploadUrlRegex = new(
        @"url:\s*'(?<url>https://[^']*?/ajax/file_upload_handler[^']*)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionIdRegex = new(
        @"_sessionid:\s*'(?<v>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrackerRegex = new(
        @"cTracker:\s*'(?<v>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The cap the server declares for THIS session, written into the uploader's
    /// <c>maxFileSize</c> option. It is the authority, not <see cref="MaxFileSize"/>: 21474836480
    /// (20 GiB) for a registered account, 1073741824 (1 GiB) for an anonymous trial account, and
    /// <b>0 for a session that isn't allowed to upload at all</b> — a plain signed-out visitor gets
    /// a complete, valid-looking upload ticket and a zero cap.
    /// </summary>
    private static readonly Regex MaxSizeRegex = new(
        @"uploaderMaxSize\s*=\s*(?<n>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The header's own account label (<c>&lt;span class="user-screen-name"&gt;</c>) — the
    /// site's chosen display name for the signed-in user, not a scraped guess at one.</summary>
    private static readonly Regex ScreenNameRegex = new(
        @"<span[^>]*class=""[^""]*user-screen-name[^""]*""[^>]*>(?<name>[^<]{1,64})</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Wording the node uses when the day's allowance is spent — <c>"Max uploads reached."</c>
    /// in <c>name</c>, <c>"You have reached the maximum permitted uploads for today."</c> in
    /// <c>error</c>. Matched on the distinctive phrases rather than on "maximum", which also appears
    /// in the size refusal.</summary>
    private static readonly string[] DailyCapPhrases = ["permitted uploads", "max uploads reached"];

    private const string DailyCapMessage =
        "Filestank's daily upload allowance for this account is used up (it allows a limited number of "
        + "files per day). Remaining files will fail until it resets — requeue them tomorrow.";

    /// <summary>Sign-in is serialised per account: without this, N parallel uploads that all start
    /// without a cookie each open their own WebView.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _signInGates = new();

    /// <summary>
    /// When each account last hit the daily upload cap. A batch is the whole point: the 11th file of
    /// 80 gets refused AFTER pushing a 100 MB chunk, and so would the other 69. Remembering it turns
    /// gigabytes of doomed transfer into instant, accurate failures.
    /// <para>
    /// Keyed on the UTC day. The server's reset boundary is its own timezone, not ours, so this can
    /// be up to a day out of step — deliberately in the forgiving direction: a stale entry costs one
    /// wasted attempt after the real reset, where over-blocking would cost the host for a whole day.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<int, DateTime> _dailyCapHitUtc = new();

    private readonly IInteractiveAuthService? _authService;
    private readonly FileHosterLoginRepository? _loginRepository;

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public FilestankPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    internal FilestankPipeline(
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

    public string Name => "Filestank";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>20 GiB — the uploader's own <c>maxFileSize</c>.</summary>
    public long? MaxFileSize => UploaderMaxSize;

    public int? MaxFilesPerPackage => null;

    /// <summary>Account-only: the node authenticates on a signed-in <c>_sessionid</c>.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // Once the account's daily upload allowance is gone, every remaining file in the batch will
        // be refused the same way — after pushing a chunk each. Fail them here instead.
        if (DailyCapAlreadyHit(ctx.Credentials.Id))
        {
            yield return new AttemptFailed(DailyCapMessage, null);
            yield break;
        }

        bool needSignIn = !HasValidStoredSession(ctx);
        if (needSignIn)
        {
            yield return new AuthStarted();
        }

        string? session = await GetOrAcquireSessionAsync(ctx, ctx.Cancellation);
        if (session is null)
        {
            if (needSignIn)
            {
                yield return new AuthFailed("Filestank sign-in was cancelled or didn't complete.");
            }

            yield return new AttemptFailed(
                "Filestank needs an account — open Settings → Accounts and sign in.",
                null);
            yield break;
        }

        if (needSignIn)
        {
            yield return new AuthSucceeded();
        }

        // === Scrape this upload's ticket (node URL + _sessionid + cTracker) ===
        (UploadTicket? ticket, string? ticketError, bool stale) = await GetUploadTicketAsync(ctx, session);

        // A session that has lapsed server-side renders the signed-out uploader.js — no ticket in it.
        // Drop the stored cookie and sign in once more, then try again.
        if (ticket is null && stale)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: stored session is no longer signed in; signing in again.");
            await ClearSessionAsync(ctx.Credentials, ctx.Cancellation);
            yield return new AuthStarted();

            string? fresh = await GetOrAcquireSessionAsync(ctx, ctx.Cancellation);
            if (fresh is null)
            {
                yield return new AuthFailed("Filestank sign-in was cancelled or didn't complete.");
                yield return new AttemptFailed("Filestank needs an account — open Settings → Accounts and sign in.", null);
                yield break;
            }

            yield return new AuthSucceeded();
            (ticket, ticketError, _) = await GetUploadTicketAsync(ctx, fresh);
        }

        if (ticket is null)
        {
            yield return new AttemptFailed(ticketError!, null);
            yield break;
        }

        // The ticket carries the cap the server declares for THIS session, which beats the static
        // MaxFileSize: it already accounts for the account's tier, and a zero means this session may
        // not upload at all. Checking it costs nothing and turns a doomed multi-GB transfer into an
        // instant, accurate refusal.
        if (SessionLimitRefusal(ticket.Value.SessionMaxFileSize, ctx.FileSize, ctx.FileName) is { } limitError)
        {
            yield return new AttemptFailed(limitError, null);
            yield break;
        }

        // === Upload ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, ticket.Value);

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

        // A transport fault propagates raw to AttemptRunner, which re-runs this pipeline and scrapes a
        // FRESH ticket — nothing is committed until the node answers, so no double-create.
        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            if (IsDailyCapRefusal(response.Body))
            {
                _dailyCapHitUtc[ctx.Credentials.Id] = DateTime.UtcNow;
                ctx.Logger.Log(this, LogType.Status, $"{Name}: daily upload allowance is spent; the rest of this batch will fail immediately.");
                yield return new AttemptFailed(DailyCapMessage, null);
                yield break;
            }

            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = apiKey;

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Filestank sign-in needs the desktop app's embedded browser.");
        }

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(BuildSignInSpec(), username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "Filestank sign-in failed: " + ex.Message);
        }

        if (captured is not InteractiveAuthResult result || string.IsNullOrEmpty(result.SessionCookieValue))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Filestank sign-in was cancelled, or didn't complete before the window was closed.");
        }

        string session = result.SessionCookieValue;
        (string? screenName, long? used, long? quota) = await ReadAccountDetailsAsync(handler, session, ct);

        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to Filestank.",
            SessionCookie: session,
            SessionCookieExpiresUtc: DateTime.UtcNow + SessionLifetime,
            PinnedProxyId: proxy.Id,
            DerivedUsername: screenName,
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>
    /// What a single upload needs, all read out of the per-session <c>uploader.js</c>. The first
    /// three rotate — the node URL's <c>csaKey</c> pair differed between two assets rendered seconds
    /// apart in the capture — so none of them can be cached across uploads.
    /// <paramref name="SessionMaxFileSize"/> is the server's cap for this session (null when the
    /// script declared none).
    /// </summary>
    internal readonly record struct UploadTicket(string UploadUrl, string SessionId, string Tracker, long? SessionMaxFileSize);

    /// <summary>
    /// Pulls the ticket out of <c>uploader.js</c>. Returns <c>Stale: true</c> when the script came
    /// back but carried no ticket — the signed-out shape, i.e. "the cookie is no good any more"
    /// rather than "the site is broken". Internal for testing.
    /// </summary>
    internal static (UploadTicket? Ticket, string? Error, bool Stale) ParseUploaderScript(string js, int statusCode)
    {
        Match url = UploadUrlRegex.Match(js);
        Match sid = SessionIdRegex.Match(js);
        Match tracker = TrackerRegex.Match(js);

        if (url.Success && sid.Success && tracker.Success)
        {
            return (new UploadTicket(
                System.Net.WebUtility.HtmlDecode(url.Groups["url"].Value),
                sid.Groups["v"].Value,
                tracker.Groups["v"].Value,
                ReadSessionMaxSize(js)), null, false);
        }

        bool stale = statusCode is 200 or 302 or 401 or 403;
        return (null,
                $"Filestank did not return an upload ticket (HTTP {statusCode}) — the sign-in may have expired.",
                stale);
    }

    /// <summary>
    /// The script declares the cap twice — <c>var uploaderMaxSize = 0;</c> up front, then the real
    /// figure inside the XHR2 branch — so the largest wins. A session that may not upload declares
    /// only the zero, which is a real answer rather than a missing one. Internal for testing.
    /// </summary>
    internal static long? ReadSessionMaxSize(string js)
    {
        long? best = null;
        foreach (Match m in MaxSizeRegex.Matches(js))
        {
            if (long.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
                && (best is null || n > best))
            {
                best = n;
            }
        }

        return best;
    }

    /// <summary>
    /// Success is a JSON array of one file object: <c>[{…,"url":…,"error":null}]</c>. blueimp also
    /// wraps it as <c>{"files":[…]}</c> depending on the handler's configuration, and YetiShare's own
    /// REST API uses <c>{"data":[…]}</c> for the same object, so all three envelopes are read. The
    /// per-file <c>error</c> is checked BEFORE the url: it rides inside an HTTP 200 and is the only
    /// place a refusal appears.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            JsonElement files = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("files", out JsonElement wrapped))
                {
                    files = wrapped;
                }
                else if (root.TryGetProperty("data", out JsonElement data))
                {
                    files = data;
                }
            }

            if (files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0 && files[0].ValueKind == JsonValueKind.Object)
            {
                JsonElement first = files[0];

                if (ReadString(first, "error") is { Length: > 0 } fileError)
                {
                    return (null, $"Filestank refused the file: {fileError}");
                }

                if (ReadString(first, "url") is { Length: > 0 } url)
                {
                    return (url, null);
                }
            }

            return (null, $"Filestank upload returned no link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
        catch (JsonException)
        {
            return (null, $"Filestank upload returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
    }

    /// <summary>Reads <c>totalActiveFileSize</c> / <c>totalFileStorage</c> out of
    /// <c>get_account_file_stats</c>. Both arrive as strings or numbers depending on the field, so
    /// both kinds are accepted. Internal for testing.</summary>
    internal static (long? Used, long? Quota) ParseAccountStats(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadLong(doc.RootElement, "totalActiveFileSize"), ReadLong(doc.RootElement, "totalFileStorage"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Reads the header's account label. Internal for testing.</summary>
    internal static string? ParseScreenName(string html)
    {
        Match m = ScreenNameRegex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string name = System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// True when the node's refusal is the daily-allowance one rather than a per-file problem. It
    /// arrives inside an HTTP 200 like every other refusal, and it is the one failure that says
    /// something about the ACCOUNT rather than about this file. Internal for testing.
    /// </summary>
    internal static bool IsDailyCapRefusal(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            JsonElement files = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out JsonElement wrapped) ? wrapped : root;
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0 || files[0].ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string haystack = $"{ReadString(files[0], "error")} {ReadString(files[0], "name")}";
            return DailyCapPhrases.Any(p => haystack.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns the session's declared cap into a refusal, or null when the file fits. Zero is its own
    /// case: the session is signed out or barred from uploading, which is a different problem from
    /// the file being too big and deserves a different sentence. Internal for testing.
    /// </summary>
    internal static string? SessionLimitRefusal(long? sessionMax, long fileSize, string fileName)
    {
        if (sessionMax is not long cap)
        {
            return null;
        }

        if (cap == 0)
        {
            return "Filestank isn't accepting uploads from this session — the sign-in may have "
                + "lapsed. Re-check the account under Settings → Accounts.";
        }

        return fileSize > cap
            ? $"{fileName} is {ByteUnit.FromBytes(fileSize, ByteBase.Binary).ToFriendlyString()}; "
                + $"Filestank's limit for this account is {ByteUnit.FromBytes(cap, ByteBase.Binary).ToFriendlyString()}."
            : null;
    }

    private bool DailyCapAlreadyHit(int credentialsId)
        => _dailyCapHitUtc.TryGetValue(credentialsId, out DateTime hitUtc) && hitUtc.Date == DateTime.UtcNow.Date;

    private static bool HasValidStoredSession(AttemptContext ctx)
    {
        bool pinMatches = ctx.Credentials.PinnedProxyId is null || ctx.Credentials.PinnedProxyId == ctx.Proxy.Id;
        return pinMatches
            && !string.IsNullOrEmpty(ctx.Credentials.SessionCookie)
            && ctx.Credentials.SessionCookieExpiresUtc is DateTime expiresUtc
            && expiresUtc > DateTime.UtcNow;
    }

    private static Dictionary<string, string> SiteHeaders(string session) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cookie"] = $"{CookieName}={session}",
        ["Referer"] = AccountUrl,
        ["X-Requested-With"] = "XMLHttpRequest",
    };

    /// <summary>
    /// Headers for the storage node. Deliberately carries NO cookie — the browser sends none either,
    /// and the node authenticates on the <c>_sessionid</c> field. Origin/Referer are what its CORS
    /// check answers to.
    /// </summary>
    private static Dictionary<string, string> NodeHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Origin"] = SiteBase,
        ["Referer"] = SiteBase + "/",
    };

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out long n) => n,
            JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) => s,
            _ => null,
        };
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
    /// The <c>filehosting</c> cookie is issued to anonymous visitors and keeps the SAME value across
    /// login, so cookie-presence would complete the WebView before the user has authenticated —
    /// hence <c>CaptureOnlyAfterLeavingLoginPage</c>, which waits for the post-login navigation to
    /// <c>/account</c>.
    /// </summary>
    private InteractiveAuthSpec BuildSignInSpec() => new(
        HosterName: Name,
        LoginUrl: LoginUrl,
        CookieDomain: CookieDomain,
        CookieName: CookieName,
        CaptureOnlyAfterLeavingLoginPage: true);

    private async Task<string?> GetOrAcquireSessionAsync(AttemptContext ctx, CancellationToken ct)
    {
        if (HasValidStoredSession(ctx))
        {
            return ctx.Credentials.SessionCookie;
        }

        if (_authService is null)
        {
            return null;
        }

        SemaphoreSlim gate = _signInGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A sibling attempt may have signed in while this one queued.
            if (HasValidStoredSession(ctx))
            {
                return ctx.Credentials.SessionCookie;
            }

            InteractiveAuthResult? captured;
            try
            {
                captured = await _authService.AcquireSessionCookieAsync(BuildSignInSpec(), ctx.Credentials.Username ?? string.Empty, ctx.Proxy, ct);
            }
            catch
            {
                return null;
            }

            if (captured is not InteractiveAuthResult result || string.IsNullOrEmpty(result.SessionCookieValue))
            {
                return null;
            }

            ctx.Credentials.SessionCookie = result.SessionCookieValue;
            ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + SessionLifetime;
            ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

            if (_loginRepository is not null)
            {
                await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
            }

            return result.SessionCookieValue;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ClearSessionAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;

        if (_loginRepository is not null)
        {
            await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
        }
    }

    private async Task<(UploadTicket? Ticket, string? Error, bool Stale)> GetUploadTicketAsync(AttemptContext ctx, string session)
    {
        // The site appends an epoch cache-buster; any changing value does the same job.
        string url = $"{UploaderScriptUrl}?r={DateTime.UtcNow.Ticks}";

        HttpResponseSnapshot snap;
        try
        {
            snap = _getOverride is not null
                ? await _getOverride(url, SiteHeaders(session))
                : await ctx.Handler.GetSnapshotAsync(url, SiteHeaders(session), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Filestank uploader lookup failed: {ex.Message}", false);
        }

        return ParseUploaderScript(snap.Body, snap.StatusCode);
    }

    private static Dictionary<string, string> BuildUploadFields(UploadTicket ticket) => new(StringComparer.Ordinal)
    {
        ["_sessionid"] = ticket.SessionId,
        ["cTracker"] = ticket.Tracker,
        ["maxChunkSize"] = MaxChunkSize.ToString(CultureInfo.InvariantCulture),
        ["folderId"] = "-1",
        ["uploadSource"] = "file_manager",
    };

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, UploadTicket ticket)
    {
        Dictionary<string, string> fields = BuildUploadFields(ticket);

        if (_uploadOverride is not null || ctx.FileSize <= MaxChunkSize)
        {
            return _uploadOverride is not null
                ? await _uploadOverride(ctx.FilePath, ticket.UploadUrl, fields, NodeHeaders(), ctx.SpeedLimitProvider)
                : await ctx.Handler.UploadMultipartAsync(
                    ctx.FilePath,
                    ticket.UploadUrl,
                    fileFieldName: FileFieldName,
                    extraFields: fields,
                    headers: NodeHeaders(),
                    getBytesPerSecond: ctx.SpeedLimitProvider,
                    cancellationToken: ctx.Cancellation);
        }

        return await UploadChunkedAsync(ctx, ticket, fields);
    }

    /// <summary>
    /// The widget's chunked mode: the same multipart body per chunk, plus <c>Content-Range</c>. Only
    /// the last chunk's response carries the link, so intermediate answers are checked for an explicit
    /// refusal and otherwise ignored. ⚠ Unverified against the live node — see the class remarks.
    /// </summary>
    private static async Task<HttpResponseSnapshot> UploadChunkedAsync(AttemptContext ctx, UploadTicket ticket, Dictionary<string, string> fields)
    {
        long total = ctx.FileSize;
        DateTime started = DateTime.Now;
        HttpResponseSnapshot last = new(0, string.Empty, Array.Empty<string>());

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

        for (long basePos = 0; basePos < total; basePos += MaxChunkSize)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();

            long len = Math.Min(MaxChunkSize, total - basePos);

            // ChunkSliceStream serves exactly `len` bytes from the shared FileStream (whose position
            // advances as each slice is consumed) and never disposes it.
            ChunkSliceStream slice = new(file, len);

            Dictionary<string, string> headers = NodeHeaders();
            headers["Content-Range"] = string.Create(
                CultureInfo.InvariantCulture, $"bytes {basePos}-{basePos + len - 1}/{total}");

            last = await ctx.Handler.PostFileChunkAsync(
                ticket.UploadUrl,
                fields,
                fileFieldName: FileFieldName,
                fileName: ctx.FileName,
                chunkData: slice,
                chunkLength: len,
                basePosition: basePos,
                totalFileSize: total,
                dateTimeStarted: started,
                headers: headers,
                getBytesPerSecond: ctx.SpeedLimitProvider,
                cancellationToken: ctx.Cancellation);

            // An explicit per-file error means the node has given up; sending the rest wastes the
            // whole upload's bandwidth. A missing url is expected on every chunk but the last.
            (string? _, string? chunkError) = ParseUploadResponse(last);
            if (chunkError is not null && basePos + len < total && LooksLikeRefusal(last.Body))
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>True when the body carries a per-file <c>error</c> string — the node's way of
    /// refusing mid-upload, as opposed to the url simply not being there yet.</summary>
    private static bool LooksLikeRefusal(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            JsonElement files = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out JsonElement wrapped) ? wrapped : root;
            return files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0
                && files[0].ValueKind == JsonValueKind.Object
                && ReadString(files[0], "error") is { Length: > 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<(string? ScreenName, long? Used, long? Quota)> ReadAccountDetailsAsync(HttpHandler handler, string session, CancellationToken ct)
    {
        string? screenName = null;
        try
        {
            HttpResponseSnapshot page = await handler.GetSnapshotAsync(AccountUrl, SiteHeaders(session), ct);
            screenName = ParseScreenName(page.Body);
        }
        catch (Exception)
        {
            // Identity and storage are decoration — never fail a good sign-in over them.
        }

        try
        {
            HttpResponseSnapshot stats = await handler.PostFormAsync(StatsUrl, new Dictionary<string, string>(StringComparer.Ordinal), SiteHeaders(session), ct);
            (long? used, long? quota) = ParseAccountStats(stats.Body);
            return (screenName, used, quota);
        }
        catch (Exception)
        {
            return (screenName, null, null);
        }
    }
}
