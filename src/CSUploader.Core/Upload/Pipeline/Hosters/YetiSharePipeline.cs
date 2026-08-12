// <copyright file="YetiSharePipeline.cs" company="CSUploader">
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
/// <b>YetiShare</b> — the file-hosting platform behind Filestank, udrop and BowFile, and the second
/// hosting family in this app after XFileSharing. Two steps per upload, and the first one is the
/// interesting one:
/// <list type="number">
///   <item><b>Scrape the ticket.</b> <c>GET /assets/js/uploader.js</c> is generated per session and
///   is the only place the node URL, <c>_sessionid</c> and <c>cTracker</c> appear together. All
///   three rotate, so this runs per upload — none of it can be cached.</item>
///   <item><b>Upload.</b> Multipart POST to that node with <c>files[]</c> plus the ticket fields.
///   The reply is a blueimp envelope: <c>[{"name":…,"error":null,"url":"…"}]</c>, and a per-file
///   <c>error</c> can sit inside an otherwise healthy-looking 200.</item>
/// </list>
/// <para>
/// <b>The node authenticates on the <c>_sessionid</c> FIELD, not on a cookie</b> — the cookie's job
/// is only to make the script hand out a ticket in the first place.
/// </para>
/// <para>
/// <b>⚠ <c>uploaderMaxSize</c> is the authority on what this session may do</b>, not any published
/// figure: it is the per-session cap the server writes into the script, and <b>0 means "this session
/// may not upload at all"</b>. A visitor who isn't allowed to upload still gets a complete,
/// valid-looking ticket — with a zero cap. That is exactly how the two modes here differ, and it is
/// read rather than assumed:
/// <list type="bullet">
///   <item><b>Guest hosts</b> (udrop, BowFile) declare a real cap to a signed-out visitor, so
///   <see cref="SupportsAnonymousUpload"/> is true and no sign-in happens.</item>
///   <item><b>Account-only hosts</b> (Filestank) declare 0, so the upload is gated behind the
///   WebView sign-in and the captured session cookie.</item>
/// </list>
/// </para>
/// <para>
/// <b>⚠ The ticket is bound to the session that issued it.</b> Scraping the script without keeping
/// the cookie its response sets yields a ticket the node answers with a 404 page — measured on udrop,
/// and the same trap on every host here.
/// </para>
/// </summary>
public abstract class YetiSharePipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    /// <summary>The site's origin, e.g. <c>https://www.udrop.com</c>. Every other URL derives from it —
    /// YetiShare's routes are fixed across installs.</summary>
    protected abstract string SiteBase { get; }

    private string LoginUrl => SiteBase + "/account/login";

    private string AccountUrl => SiteBase + "/account";

    private string StatsUrl => SiteBase + "/account/ajax/get_account_file_stats";

    /// <summary>Server-generated per session — the only place the node URL, <c>_sessionid</c> and
    /// <c>cTracker</c> appear together.</summary>
    private string UploaderScriptUrl => SiteBase + "/assets/js/uploader.js";

    /// <summary>The platform's session cookie. Same name across the installs seen so far, and it is
    /// what <c>uploader.js</c> sets for a guest as well as what a sign-in captures.</summary>
    protected virtual string CookieName => "filehosting";

    /// <summary>Cookie domain for the sign-in capture; defaults to the site's own host.</summary>
    protected virtual string CookieDomain => new Uri(SiteBase).Host;

    /// <summary>
    /// Opt-in: this install's sign-in is a plain form this app can post itself, so no browser is
    /// needed. Default false — Filestank ships on the WebView and its login has not been shown to
    /// work headlessly, and a wrong guess here is a sign-in that silently never succeeds.
    /// </summary>
    protected virtual bool SupportsDirectLogin => false;

    /// <summary>
    /// Signs in by posting the account form, returning the session cookie to use afterwards.
    /// <para>
    /// <b>⚠ The reply sets no cookie.</b> The platform UPGRADES the session the request already
    /// carried — so the cookie handed out by <c>GET /account/login</c> is the one that becomes
    /// authenticated, and it is what this returns. Looking for a fresh <c>Set-Cookie</c> on the 302
    /// finds nothing and reads as a failed login.
    /// </para>
    /// <para>
    /// Success is a <b>302</b> to <c>/account</c>; a wrong password re-renders the form as a
    /// <b>200</b>. There is no error envelope, so that is the whole signal.
    /// </para>
    /// </summary>
    private async Task<(string? Session, string? Error)> DirectLoginAsync(HttpHandler handler, string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, $"{Name} needs a username and password.");
        }

        string? session;
        try
        {
            HttpResponseSnapshot page = await handler.GetSnapshotAsync(LoginUrl, null, ct).ConfigureAwait(false);
            session = ExtractCookie(page.SetCookies, CookieName);
        }
        catch (Exception ex)
        {
            return (null, $"{Name} login page fetch failed: {ex.Message}");
        }

        if (session is null)
        {
            return (null, $"{Name} login page issued no session cookie.");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["username"] = username,
            ["password"] = password,
            ["submitme"] = "1",
        };

        HttpResponseSnapshot login;
        try
        {
            login = await handler.PostFormAsync(LoginUrl, form, SiteHeaders(session), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"{Name} login request failed: {ex.Message}");
        }

        return login.StatusCode is >= 300 and < 400
            ? (session, null)
            : (null, $"{Name} sign-in failed — check the username and password.");
    }

    /// <summary>blueimp's field name, as sent by the site.</summary>
    private const string FileFieldName = "files[]";

    /// <summary>The uploader's own <c>maxChunkSize</c>. Files at or below this go in one POST.</summary>
    private const long MaxChunkSize = 100_000_000;

    /// <summary>The uploader's own <c>maxFileSize</c> for this host — the fallback shown before an
    /// upload runs. The live per-session figure in the script still wins at upload time.</summary>
    protected abstract long UploaderMaxSize { get; }

    /// <summary>The node's <c>Set-Cookie</c> gives the session 24 h; expire ours earlier so an
    /// upload never starts against a session about to lapse mid-transfer.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(18);

    private static readonly Regex UploadUrlRegex = new(
        @"url:\s*'(?<url>https://[^']*?/ajax/file_upload_handler[^']*)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The other shape this platform ships: instead of a literal <c>url:</c>, the script declares a
    /// <b>pool</b> of nodes and picks one at random —
    /// <code>
    /// const uploadEndpoints = ["https:\/\/f116.mupload.store\/ajax\/file_upload_handler?…"];
    /// …
    /// url: getUploadEndpoint(),
    /// </code>
    /// So <c>url:</c> is a call, not an address, and a host on this variant reads as "no upload
    /// ticket" to a parser that only knows the literal form (MegaUp did, until this was added). The
    /// members are JSON, so their slashes arrive escaped.
    /// </summary>
    private static readonly Regex UploadPoolRegex = new(
        @"uploadEndpoints\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UploadPoolMemberRegex = new(
        @"""(?<url>https:(?:\\/|/){2}[^""]*?/ajax(?:\\/|/)file_upload_handler[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>What the user is told once the day's allowance is spent. Per host, because the
    /// allowance is the host's policy, not the platform's.</summary>
    private string DailyCapMessage =>
        $"{Name}'s daily upload allowance for this account is used up (it allows a limited number of "
        + "uploads per day). The remaining files in this package were not attempted; try again "
        + "tomorrow, or upload them to another hoster.";

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

    protected YetiSharePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
    {
        _authService = authService;
        _loginRepository = loginRepository;
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    private protected YetiSharePipeline(
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

    public abstract string Name { get; }

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>20 GiB — the uploader's own <c>maxFileSize</c>.</summary>
    public long? MaxFileSize => UploaderMaxSize;

    /// <summary>
    /// <inheritdoc cref="IFileHosterPipeline.RetentionFor" path="/summary/text()[1]"/>
    /// <para>
    /// Declared <c>virtual</c> here because the interface slot binds at this class — a subclass method
    /// sharing the name alone would never be called through <see cref="IFileHosterPipeline"/>.
    /// Left unspecified for the family: udrop states its storage is permanent, but that is udrop's
    /// policy, not YetiShare's, and the other installs here say nothing either way.
    /// </para>
    /// </summary>
    public virtual FileRetention RetentionFor(FileHosterLoginDto credentials) => FileRetention.Unspecified;

    public int? MaxFilesPerPackage => null;

    /// <summary>Account-only: the node authenticates on a signed-in <c>_sessionid</c>.</summary>
    /// <summary>True on installs that let a signed-out visitor upload — read from the script's
    /// per-session cap rather than guessed; see the class remarks.</summary>
    public virtual bool SupportsAnonymousUpload => false;

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

        // A guest upload has no sign-in step at all: the uploader script issues a session to whoever
        // asks, and the cap it declares is what says whether that session may upload (0 = it may not,
        // which is how the account-only hosts in this family present themselves).
        bool guest = SupportsAnonymousUpload && ctx.Credentials.IsAnonymous;

        bool needSignIn = !guest && !HasValidStoredSession(ctx);
        if (needSignIn)
        {
            yield return new AuthStarted();
        }

        string? session = guest ? null : await GetOrAcquireSessionAsync(ctx, ctx.Cancellation);
        if (!guest && session is null)
        {
            if (needSignIn)
            {
                yield return new AuthFailed($"{Name} sign-in was cancelled or didn't complete.");
            }

            yield return new AttemptFailed(
                $"{Name} needs an account — open Settings → Accounts and sign in.",
                null);
            yield break;
        }

        if (needSignIn)
        {
            yield return new AuthSucceeded();
        }

        // === Scrape this upload's ticket (node URL + _sessionid + cTracker) ===
        (UploadTicket? ticket, string? ticketError, bool stale, string? issued) = await GetUploadTicketAsync(ctx, session);
        session = issued ?? session;

        // A session that has lapsed server-side renders the signed-out uploader.js — no ticket in it.
        // Drop the stored cookie and sign in once more, then try again.
        if (ticket is null && stale && !guest)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: stored session is no longer signed in; signing in again.");
            await ClearSessionAsync(ctx.Credentials, ctx.Cancellation);
            yield return new AuthStarted();

            string? fresh = await GetOrAcquireSessionAsync(ctx, ctx.Cancellation);
            if (fresh is null)
            {
                yield return new AuthFailed($"{Name} sign-in was cancelled or didn't complete.");
                yield return new AttemptFailed($"{Name} needs an account — open Settings → Accounts and sign in.", null);
                yield break;
            }

            yield return new AuthSucceeded();
            (ticket, ticketError, _, string? reissued) = await GetUploadTicketAsync(ctx, fresh);
            session = reissued ?? fresh;
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
        if (SessionLimitRefusal(Name, ticket.Value.SessionMaxFileSize, ctx.FileSize, ctx.FileName) is { } limitError)
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

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, ticket.Value, session);

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

        (string? url, string? error) = ParseUploadResponse(Name, response);
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

        // A host whose login this app can post signs in with no browser at all.
        if (SupportsDirectLogin)
        {
            (string? direct, string? loginError) = await DirectLoginAsync(handler, username, password, ct).ConfigureAwait(false);
            if (direct is null)
            {
                return new AccountCheckResult(false, AccountType.Free, loginError ?? $"{Name} sign-in failed.");
            }

            (string? _, long? usedBytes, long? quotaBytes) = await ReadAccountDetailsAsync(handler, direct, ct).ConfigureAwait(false);
            return new AccountCheckResult(
                true,
                AccountType.Free,
                $"Signed in to {Name}.",
                SessionCookie: direct,
                SessionCookieExpiresUtc: DateTime.UtcNow + SessionLifetime,
                PinnedProxyId: proxy.Id,

                // The USERNAME THAT WAS TYPED, never the page's screen name. The verifier's
                // DerivedUsername is written straight onto the account's Username, which for a
                // direct-login host is the identifier the next sign-in posts — and the two are not
                // interchangeable: MegaUp's screen name "Lynford" belongs to the account whose login
                // is "LynfordAudie", and posting the former returns the login form (measured). The
                // screen name is read here only for the WebView path below, where the user typed no
                // username and it is the only identity available.
                DerivedUsername: username,
                StorageUsedBytes: usedBytes,
                StorageQuotaBytes: quotaBytes);
        }

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                $"{Name} sign-in needs the desktop app's embedded browser.");
        }

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(BuildSignInSpec(), username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, $"{Name} sign-in failed: " + ex.Message);
        }

        if (captured is not InteractiveAuthResult result || string.IsNullOrEmpty(result.SessionCookieValue))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                $"{Name} sign-in was cancelled, or didn't complete before the window was closed.");
        }

        string session = result.SessionCookieValue;
        (string? screenName, long? used, long? quota) = await ReadAccountDetailsAsync(handler, session, ct);

        return new AccountCheckResult(
            true,
            AccountType.Free,
            $"Signed in to {Name}.",
            SessionCookie: session,
            SessionCookieExpiresUtc: DateTime.UtcNow + SessionLifetime,
            PinnedProxyId: proxy.Id,
            DerivedUsername: screenName,
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>
    /// Re-checks the account with the session already stored, so a save or a "Check / Refresh" doesn't
    /// reopen the sign-in window seconds after the user signed in. The session cookie IS the credential
    /// here, so a lapsed one reports invalid and says to sign in again rather than silently reopening
    /// the browser — the user should know the account has run out, not just watch a window reappear.
    /// </summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;

        (string? screenName, long? used, long? quota) = await ReadAccountDetailsAsync(handler, sessionCookie, ct).ConfigureAwait(false);

        // ReadAccountDetailsAsync swallows its own failures (identity and storage are decoration on a
        // fresh sign-in), so "nothing came back at all" is the only signal a stale session leaves.
        if (screenName is null && used is null && quota is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                $"The saved {Name} session is no longer valid — sign in again.");
        }

        return new AccountCheckResult(
            true,
            AccountType.Free,
            $"Signed in to {Name}.",
            SessionCookie: sessionCookie,
            SessionCookieExpiresUtc: DateTime.UtcNow + SessionLifetime,
            PinnedProxyId: proxy.Id,

            // Null for a direct-login host, so the stored Username — the identifier its next sign-in
            // posts — survives. This runs on EVERY refresh, so returning the page's screen name here
            // would quietly replace the login with a name that can't authenticate. A WebView host has
            // no typed username to protect, and there the screen name is the only identity there is.
            DerivedUsername: SupportsDirectLogin ? null : screenName,
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
    internal static (UploadTicket? Ticket, string? Error, bool Stale) ParseUploaderScript(string host, string js, int statusCode)
    {
        string? url = ReadNodeUrl(js);
        Match sid = SessionIdRegex.Match(js);
        Match tracker = TrackerRegex.Match(js);

        if (url is not null && sid.Success && tracker.Success)
        {
            return (new UploadTicket(
                url,
                sid.Groups["v"].Value,
                tracker.Groups["v"].Value,
                ReadSessionMaxSize(js)), null, false);
        }

        bool stale = statusCode is 200 or 302 or 401 or 403;
        return (null,
                $"{host} did not return an upload ticket (HTTP {statusCode}) — the sign-in may have expired.",
                stale);
    }

    /// <summary>
    /// The node this upload should go to: the literal <c>url:</c> when the script has one, otherwise
    /// a member of the <c>uploadEndpoints</c> pool.
    /// <para>
    /// The pool member is chosen <b>at random</b>, exactly as the site's own
    /// <c>getUploadEndpoint()</c> does. Taking the first instead would send every upload from every
    /// user to the same box — and a dead pool member stays dead, which is the failure DailyUploads
    /// demonstrated on the xfspro side.
    /// </para>
    /// Internal for testing.
    /// </summary>
    internal static string? ReadNodeUrl(string js)
    {
        if (UploadUrlRegex.Match(js) is { Success: true } literal)
        {
            return System.Net.WebUtility.HtmlDecode(literal.Groups["url"].Value);
        }

        if (UploadPoolRegex.Match(js) is not { Success: true } pool)
        {
            return null;
        }

        List<string> members = [.. UploadPoolMemberRegex.Matches(pool.Groups["body"].Value)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["url"].Value).Replace("\\/", "/", StringComparison.Ordinal))];

        return members.Count == 0 ? null : members[Random.Shared.Next(members.Count)];
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
    internal static (string? Url, string? Error) ParseUploadResponse(string host, HttpResponseSnapshot response)
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
                    return (null, $"{host} refused the file: {fileError}");
                }

                if (ReadString(first, "url") is { Length: > 0 } url)
                {
                    return (url, null);
                }
            }

            return (null, $"{host} upload returned no link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
        catch (JsonException)
        {
            return (null, $"{host} upload returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
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
    internal static string? SessionLimitRefusal(string host, long? sessionMax, long fileSize, string fileName)
    {
        if (sessionMax is not long cap)
        {
            return null;
        }

        if (cap == 0)
        {
            return $"{host} isn't accepting uploads from this session — the sign-in may have "
                + "lapsed. Re-check the account under Settings → Accounts.";
        }

        return fileSize > cap
            ? $"{fileName} is {ByteUnit.FromBytes(fileSize, ByteBase.Binary).ToFriendlyString()}; "
                + $"{host}'s limit for this account is {ByteUnit.FromBytes(cap, ByteBase.Binary).ToFriendlyString()}."
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

    private Dictionary<string, string> SiteHeaders(string session) => new(StringComparer.OrdinalIgnoreCase)
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
    /// <summary>
    /// Headers for the node POST.
    /// <para>
    /// <b>Whether the session cookie belongs here depends on where the node lives</b>, and both shapes
    /// are in this family. A node on a SEPARATE host (Filestank's <c>strN.</c>, BowFile's <c>fsNN.</c>)
    /// is a storage box that authenticates on the <c>_sessionid</c> FIELD and needs no cookie — the
    /// site's cookie is host-only and wouldn't reach it anyway. A node on the SAME host as the site
    /// (udrop) is an ordinary site route behind the session middleware, and without the cookie it
    /// answers a <b>404 page</b>. Measured on all three.
    /// </para>
    /// </summary>
    private Dictionary<string, string> NodeHeaders(string? session, string uploadUrl)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Origin"] = SiteBase,
            ["Referer"] = SiteBase + "/",
            ["Accept"] = "application/json, text/javascript, */*; q=0.01",
            ["X-Requested-With"] = "XMLHttpRequest",
        };

        if (session is not null
            && Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? node)
            && string.Equals(node.Host, new Uri(SiteBase).Host, StringComparison.OrdinalIgnoreCase))
        {
            headers["Cookie"] = $"{CookieName}={session}";
        }

        return headers;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads a byte count that the platform encodes as a STRING ("1073741824") as often as a
    /// number, so both shapes are accepted.</summary>
    private static long? ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : null,
            JsonValueKind.String => long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : null,
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

        // No browser needed where the form can be posted; without that, no auth service means no
        // way to sign in at all.
        if (_authService is null && !SupportsDirectLogin)
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

            string? acquired;
            if (SupportsDirectLogin)
            {
                (acquired, string? _) = await DirectLoginAsync(
                    ctx.Handler, ctx.Credentials.Username, ctx.Credentials.Password, ct).ConfigureAwait(false);
            }
            else
            {
                InteractiveAuthResult? captured;
                try
                {
                    captured = await _authService!.AcquireSessionCookieAsync(BuildSignInSpec(), ctx.Credentials.Username ?? string.Empty, ctx.Proxy, ct);
                }
                catch
                {
                    return null;
                }

                acquired = captured is InteractiveAuthResult result && !string.IsNullOrEmpty(result.SessionCookieValue)
                    ? result.SessionCookieValue
                    : null;
            }

            if (acquired is null)
            {
                return null;
            }

            ctx.Credentials.SessionCookie = acquired;
            ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + SessionLifetime;
            ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

            if (_loginRepository is not null)
            {
                await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
            }

            return acquired;
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

    /// <summary>
    /// Scrapes this upload's ticket. <paramref name="session"/> is null for a guest, in which case the
    /// script's own response issues a session and it is read back out — a guest still has a session,
    /// it just never signed in.
    /// </summary>
    private async Task<(UploadTicket? Ticket, string? Error, bool Stale, string? Session)> GetUploadTicketAsync(AttemptContext ctx, string? session)
    {
        // The site appends an epoch cache-buster; any changing value does the same job.
        string url = $"{UploaderScriptUrl}?r={DateTime.UtcNow.Ticks}";
        IReadOnlyDictionary<string, string>? headers = session is null ? null : SiteHeaders(session);

        HttpResponseSnapshot snap;
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
            return (null, $"{Name} uploader lookup failed: {ex.Message}", false, null);
        }

        (UploadTicket? ticket, string? error, bool stale) = ParseUploaderScript(Name, snap.Body, snap.StatusCode);
        return (ticket, error, stale, session ?? ExtractCookie(snap.SetCookies, CookieName));
    }

    /// <summary>Reads a named cookie out of a response's <c>Set-Cookie</c> list.</summary>
    private static string? ExtractCookie(IReadOnlyList<string> setCookies, string name)
    {
        string prefix = name + "=";
        foreach (string raw in setCookies)
        {
            if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string after = raw[prefix.Length..];
            int semi = after.IndexOf(';', StringComparison.Ordinal);
            string value = semi < 0 ? after : after[..semi];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static Dictionary<string, string> BuildUploadFields(UploadTicket ticket) => new(StringComparer.Ordinal)
    {
        ["_sessionid"] = ticket.SessionId,
        ["cTracker"] = ticket.Tracker,
        ["maxChunkSize"] = MaxChunkSize.ToString(CultureInfo.InvariantCulture),
        ["folderId"] = "-1",
        ["uploadSource"] = "file_manager",
    };

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, UploadTicket ticket, string? session)
    {
        Dictionary<string, string> fields = BuildUploadFields(ticket);

        if (_uploadOverride is not null || ctx.FileSize <= MaxChunkSize)
        {
            return _uploadOverride is not null
                ? await _uploadOverride(ctx.FilePath, ticket.UploadUrl, fields, NodeHeaders(session, ticket.UploadUrl), ctx.SpeedLimitProvider)
                : await ctx.Handler.UploadMultipartAsync(
                    ctx.FilePath,
                    ticket.UploadUrl,
                    fileFieldName: FileFieldName,
                    extraFields: fields,
                    headers: NodeHeaders(session, ticket.UploadUrl),
                    getBytesPerSecond: ctx.SpeedLimitProvider,
                    cancellationToken: ctx.Cancellation);
        }

        return await UploadChunkedAsync(ctx, ticket, fields, session);
    }

    /// <summary>
    /// The widget's chunked mode: the same multipart body per chunk, plus <c>Content-Range</c>. Only
    /// the last chunk's response carries the link, so intermediate answers are checked for an explicit
    /// refusal and otherwise ignored. ⚠ Unverified against the live node — see the class remarks.
    /// </summary>
    private async Task<HttpResponseSnapshot> UploadChunkedAsync(AttemptContext ctx, UploadTicket ticket, Dictionary<string, string> fields, string? session)
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

            Dictionary<string, string> headers = NodeHeaders(session, ticket.UploadUrl);
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
            (string? _, string? chunkError) = ParseUploadResponse(Name, last);
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

    private async Task<(string? ScreenName, long? Used, long? Quota)> ReadAccountDetailsAsync(HttpHandler handler, string session, CancellationToken ct)
    {
        string? screenName = null;
        try
        {
            // Through the same seam the ticket scrape uses, so the identity/storage read — and the
            // rule about which name may leave this method — is testable without a network.
            HttpResponseSnapshot page = _getOverride is not null
                ? await _getOverride(AccountUrl, SiteHeaders(session))
                : await handler.GetSnapshotAsync(AccountUrl, SiteHeaders(session), ct);
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
