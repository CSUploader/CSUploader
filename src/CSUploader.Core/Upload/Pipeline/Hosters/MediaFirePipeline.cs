// <copyright file="MediaFirePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// MediaFire (mediafire.com) account upload pipeline. MediaFire's own web app talks to a REST API
/// (<c>/api/1.5/*</c>) that authenticates by a short-lived <b>session token</b>, so the flow is a web
/// login (to get the durable <c>user</c> cookie) → session token → SHA-256 hash-dedup check → either an
/// instant link (server already has the bytes) or a raw byte upload + poll. Verified against a live
/// capture (2026-07-03) plus live endpoint probing 2026-07-04.
/// <list type="number">
///   <item><b>Login (cookie).</b> GET <c>/login/</c> sets session cookies and carries a hidden
///   <c>&lt;input name="security" value="TS.HASH"&gt;</c>. POST
///   <c>/dynamic/client_login/mediafire.php</c> (urlencoded <c>security</c>/<c>login_email</c>/
///   <c>login_pass</c>/<c>login_remember=yes</c>) forwarding those cookies → a <c>Set-Cookie: user=…</c>
///   on success. A wrong password re-renders a JSON error (<c>action:10</c>, "invalid login"); a stale
///   token/cookie gives <c>action:-50</c> ("form idle too long"). This login is NOT captcha-gated for a
///   normal account, so it runs in C# with no WebView. The merged cookie jar is cached per credentials
///   id (one login per account, not per file).</item>
///   <item><b>Session token.</b> POST <c>/application/get_session_token.php</c> (empty body) with the
///   <c>user</c> cookie → <c>{"response":{"session_token":"&lt;128-hex&gt;"}}</c>. A v1 (non-rotating)
///   token; fetched fresh per upload. Every subsequent <c>/api/1.5/*</c> call authenticates by this
///   token in the body — NOT by a cookie.</item>
///   <item><b>Hash check.</b> Compute the file's SHA-256 (MediaFire's dedup key; the app's shared hash
///   is MD5 so this pipeline hashes the file itself). POST <c>/api/1.5/upload/check.php</c> (urlencoded
///   <c>uploads</c> = a one-element JSON array, <c>response_format=json</c>, <c>session_token</c>) →
///   <c>hash_exists</c>, <c>storage_limit_exceeded</c>, <c>available_space</c> and the
///   <c>upload_url.simple</c> byte-upload endpoint.</item>
///   <item><b>Instant (dedup).</b> When <c>hash_exists=="yes"</c>, POST
///   <c>/api/1.5/upload/instant.php</c> (urlencoded <c>filename</c>/<c>folder_key=myfiles</c>/
///   <c>size</c>/<c>hash</c>/<c>session_token</c>) → a <c>quickkey</c> with no byte transfer.</item>
///   <item><b>Simple (byte upload).</b> When <c>hash_exists=="no"</c>, POST the raw file bytes to
///   <c>upload_url.simple</c> (query <c>?session_token=…&amp;response_format=json</c>) with headers
///   <c>x-filename</c>/<c>x-filesize</c>/<c>x-filehash</c> and Content-Type
///   <c>application/octet-stream</c> — the server checks <c>x-filesize</c> against the exact body length,
///   so it must be a raw body, never multipart. The response carries <c>doupload.key</c>; poll
///   <c>/api/1.5/upload/poll_upload.php</c> until <c>doupload</c> reports a <c>quickkey</c>.</item>
///   <item><b>Result.</b> The share link is <c>https://www.mediafire.com/file/&lt;quickkey&gt;</c>.</item>
/// </list>
/// Storage: <c>/api/1.5/user/get_info.php</c> (<c>session_token</c>) returns <c>used_storage_size</c> and
/// <c>storage_limit</c>, surfaced via <see cref="CheckAccountAsync"/> + <see cref="IStorageRefreshablePipeline"/>.
/// <para><b>Verification status.</b> Every request shape and every error shape is verified live; the
/// authenticated success shapes for login / session-token / check / instant come straight from the
/// capture. The one leg NOT observed end-to-end is a real byte upload (simple.php + poll_upload.php) —
/// the capture's file hash-existed so it went instant. The simple.php request contract (required headers,
/// raw body, size match) was reverse-engineered live from the endpoint's own error responses; its SUCCESS
/// response (<c>doupload.key</c>) and the poll loop follow MediaFire's documented shape and want a live
/// round-trip with a real account to confirm.</para>
/// </summary>
public sealed partial class MediaFirePipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    private const string Host = "https://www.mediafire.com";
    private const string LoginPageUrl = Host + "/login/";
    private const string ClientLoginUrl = Host + "/dynamic/client_login/mediafire.php";
    private const string SessionTokenUrl = Host + "/application/get_session_token.php";
    private const string ApiBase = Host + "/api/1.5";
    private const string CheckUrl = ApiBase + "/upload/check.php";
    private const string InstantUrl = ApiBase + "/upload/instant.php";
    private const string PollUrl = ApiBase + "/upload/poll_upload.php";
    private const string UserInfoUrl = ApiBase + "/user/get_info.php";
    private const string FilePublicPrefix = Host + "/file/";

    /// <summary>Root folder key MediaFire's web app uses for "My Files" (from the capture). Uploads
    /// land at the account root; MediaFire has no per-file-count limit for a package.</summary>
    private const string RootFolderKey = "myfiles";

    // The web app posts app.mediafire.com Origin/Referer on its API calls; mirror them so nothing on
    // the server side rejects a bare client. CORS isn't enforced our side — these are belt-and-braces.
    private const string AppOrigin = "https://app.mediafire.com";

    // Poll cadence for the async byte-upload assembly step (simple.php → poll_upload.php). MediaFire
    // assembles almost immediately; cap the wait so a stuck key fails rather than hanging the attempt.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxPollAttempts = 40; // ≈80 s ceiling

    // Hidden <input type="hidden" name="security" value="TS.HASH"> on the login page. name precedes
    // value in the live markup; anchored on name="security" to avoid the google/facebook variants.
    private static readonly Regex _securityTokenRegex = MyRegex();

    // The merged login cookie jar (the durable `user` cookie + whatever /login/ set), cached per
    // credentials id. One login at a time per id so a batch of N files does ONE login, not N.
    private readonly ConcurrentDictionary<int, string> _cookieJarByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    // ONE session token shared across all uploads for an account. MediaFire caps the number of active
    // session tokens per account and invalidates older ones when new ones are minted — so minting a
    // fresh token per file made concurrent uploads knock each other's tokens out ("session token
    // expired or invalid", error 105). Mirroring MediaFire's own web app, all parallel uploads reuse
    // one token, refreshed on demand when it genuinely expires. Gated per id so N files mint it once.
    private readonly ConcurrentDictionary<int, string> _sessionTokenByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _tokenGates = new();

    // MediaFire's error code for an expired/invalid session token — the trigger to refresh + retry.
    private const int SessionExpiredError = 105;

    // Test seams — null in production (use the real handler). Route by URL in tests.
    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, CancellationToken, Task<string>>? _computeHashOverride;

    public MediaFirePipeline()
    {
    }

    /// <summary>Test ctor — stubs the three HTTP shapes (GET, urlencoded POST, raw-body upload) and the
    /// SHA-256 computation so the whole login → token → check → instant/simple → poll orchestration runs
    /// without the network or a real file on disk.</summary>
    internal MediaFirePipeline(
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, IReadOnlyDictionary<string, string>, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, CancellationToken, Task<string>> computeHashOverride)
    {
        _getOverride = getOverride;
        _postFormOverride = postFormOverride;
        _uploadOverride = uploadOverride;
        _computeHashOverride = computeHashOverride;
    }

    public string Name => "MediaFire";

    /// <summary>Downloads are captcha-free: the file page embeds a direct CDN href in its
    /// initial HTML with no captcha widget (live page, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>MediaFire dedups on SHA-256, but the app's shared hasher is MD5, so this pipeline
    /// computes its own hash — it does NOT rely on the runner's pre-hash.</summary>
    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No hard per-file cap declared — MediaFire's per-file limit varies with tier/validation,
    /// and <c>upload/check.php</c> reports <c>storage_limit_exceeded</c> against live free space, which
    /// this pipeline honours as a real pre-flight before any bytes are sent.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>MediaFire uploads require an account (email/password). No anonymous option.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: the account's SHARED session token (logs in + caches the cookie jar on first use;
        // all parallel uploads reuse ONE token — see _sessionTokenByCredId) ===
        (string? sessionToken, string? authError) = await MintSharedTokenAsync(ctx.Handler, ctx.Credentials, invalidToken: null, ctx.Cancellation);
        if (sessionToken is null)
        {
            yield return new AttemptFailed(authError ?? "MediaFire sign-in failed.", null);
            yield break;
        }

        // === Step 2: SHA-256 (MediaFire's dedup key) ===
        string? hash = null;
        Exception? hashEx = null;
        try
        {
            hash = await ComputeSha256Async(ctx.FilePath, ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            hashEx = ex;
        }

        if (hash is null)
        {
            yield return new AttemptFailed("Could not read the file to hash it: " + (hashEx?.Message ?? "unknown error"), hashEx);
            yield break;
        }

        // === Step 3: hash-dedup check (refresh the shared token once if it expired) ===
        (CheckOutcome? check, bool checkExpired, string? checkError) = await CheckAsync(ctx, sessionToken, hash);
        if (checkExpired)
        {
            (sessionToken, authError) = await MintSharedTokenAsync(ctx.Handler, ctx.Credentials, invalidToken: sessionToken, ctx.Cancellation);
            if (sessionToken is null)
            {
                yield return new AttemptFailed(authError ?? "MediaFire session refresh failed.", null);
                yield break;
            }

            (check, checkExpired, checkError) = await CheckAsync(ctx, sessionToken, hash);
        }

        if (check is null)
        {
            yield return new AttemptFailed(checkError ?? "MediaFire upload check failed.", null);
            yield break;
        }

        if (check.StorageExceeded)
        {
            string have = check.AvailableSpace is { } a
                ? ByteUnit.FromBytes(a, ByteBase.Binary).ToFriendlyString()
                : "the remaining";
            yield return new AttemptFailed(
                $"Not enough MediaFire storage for this file ({ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}) — {have} free.",
                null);
            yield break;
        }

        // === Step 4a: instant (server already has these bytes) ===
        yield return new TransferStarted(ctx.FileSize);

        if (check.HashExists)
        {
            (string? instantKey, bool instantExpired, string? instantError) = await InstantAsync(ctx, sessionToken, hash);
            if (instantExpired)
            {
                (sessionToken, authError) = await MintSharedTokenAsync(ctx.Handler, ctx.Credentials, invalidToken: sessionToken, ctx.Cancellation);
                if (sessionToken is null)
                {
                    yield return new AttemptFailed(authError ?? "MediaFire session refresh failed.", null);
                    yield break;
                }

                (instantKey, instantExpired, instantError) = await InstantAsync(ctx, sessionToken, hash);
            }

            if (instantKey is null)
            {
                yield return new AttemptFailed(instantError ?? "MediaFire instant upload failed.", null);
                yield break;
            }

            yield return new TransferCompleted(FilePublicPrefix + instantKey);
            yield break;
        }

        // === Step 4b: simple byte upload + poll ===
        string simpleUrl = check.SimpleUrl ?? "https://www.mediafireuserupload.com/api/upload/simple.php";

        string? pollKey = null;
        string? directQuickKey = null;
        string? uploadError = null;
        bool uploadExpired = false;

        // Up to two attempts: if MediaFire reports the session token expired (error 105), refresh the
        // shared token and re-send the bytes once. With the shared token this is rare (nothing
        // invalidates it concurrently) — it mainly covers a token that aged out during a long transfer.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // Bridge HttpHandler.UploadProgress -> TransferProgress via a channel (can't yield from inside
            // the event handler) — same pattern as the other streaming pipelines.
            Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void OnProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += OnProgress;

            Task<HttpResponseSnapshot> uploadTask = SimpleUploadAsync(ctx, simpleUrl, sessionToken, hash);
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

            // Let a transport fault propagate to the shared retry layer: until the body is fully sent
            // MediaFire has committed nothing, so it arrives as a retryable UploadBodyTransferException and
            // a whole-pipeline retry re-checks the hash — if the bytes DID land, check.php now returns
            // hash_exists and we go instant to the SAME quickkey, so a retry never double-creates. A server
            // verdict does NOT throw (the snapshot returns) and flows through ParseSimpleUpload below.
            HttpResponseSnapshot uploadResponse = await uploadTask;

            (pollKey, directQuickKey, uploadExpired, uploadError) = ParseSimpleUpload(uploadResponse);
            if (uploadExpired && attempt == 0)
            {
                (sessionToken, authError) = await MintSharedTokenAsync(ctx.Handler, ctx.Credentials, invalidToken: sessionToken, ctx.Cancellation);
                if (sessionToken is null)
                {
                    yield return new AttemptFailed(authError ?? "MediaFire session refresh failed.", null);
                    yield break;
                }

                continue; // re-send the bytes with the fresh token
            }

            break;
        }

        if (uploadError is not null)
        {
            yield return new AttemptFailed(uploadError, null);
            yield break;
        }

        // Some responses already carry the quickkey; otherwise poll with the upload key.
        if (!string.IsNullOrEmpty(directQuickKey))
        {
            yield return new TransferCompleted(FilePublicPrefix + directQuickKey);
            yield break;
        }

        (string? polledKey, bool pollExpired, string? pollError) = await PollForQuickKeyAsync(ctx, sessionToken, pollKey!);
        if (pollExpired)
        {
            (sessionToken, authError) = await MintSharedTokenAsync(ctx.Handler, ctx.Credentials, invalidToken: sessionToken, ctx.Cancellation);
            if (sessionToken is null)
            {
                yield return new AttemptFailed(authError ?? "MediaFire session refresh failed.", null);
                yield break;
            }

            (polledKey, pollExpired, pollError) = await PollForQuickKeyAsync(ctx, sessionToken, pollKey!);
        }

        if (polledKey is null)
        {
            yield return new AttemptFailed(pollError ?? "MediaFire did not finish processing the upload.", null);
            yield break;
        }

        yield return new TransferCompleted(FilePublicPrefix + polledKey);
    }

    /// <summary>
    /// Verifies a MediaFire account by logging in and reading storage off
    /// <c>user/get_info.php</c>. Not cached — the Settings UI passes freshly-typed, possibly-unsaved
    /// credentials.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? sessionToken, string? error) = await MintFreshTokenAsync(handler, username, password, ct);
        if (sessionToken is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "MediaFire sign-in failed.");
        }

        (long? used, long? limit, bool premium) = await TryReadUserInfoAsync(handler, sessionToken, ct);
        return new AccountCheckResult(
            true,
            premium ? AccountType.Premium : AccountType.Free,
            premium ? "Signed in (Premium)" : "Signed in (Free)",
            DerivedUsername: username,
            StorageUsedBytes: used,
            StorageQuotaBytes: limit);
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: a fresh credential login (no
    /// captcha/WebView) plus the <c>user/get_info.php</c> read. Returns null on any failure so the
    /// caller keeps the last-known snapshot.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy;

        (string? sessionToken, _) = await MintFreshTokenAsync(handler, credentials.Username, credentials.Password, ct);
        if (sessionToken is null)
        {
            return null;
        }

        (long? used, long? limit, _) = await TryReadUserInfoAsync(handler, sessionToken, ct);
        return used is null && limit is null ? null : new StorageUsage(used, limit);
    }

    // ── Auth ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the account's SHARED session token, minting it once (gated per credentials id) on a miss.
    /// Pass the token you just saw rejected as <paramref name="invalidToken"/> to force a refresh — the
    /// gate makes N attempts that all hit an expired token re-mint exactly once (the rest observe the
    /// fresh one). A mint failure drops the cached cookie jar so the next attempt re-logs-in.
    /// </summary>
    private async Task<(string? Token, string? Error)> MintSharedTokenAsync(HttpHandler handler, FileHosterLoginDto creds, string? invalidToken, CancellationToken ct)
    {
        int id = creds.Id;

        // Fast path: a cached token that isn't the one we know is stale.
        if (_sessionTokenByCredId.TryGetValue(id, out string? cached) && cached != invalidToken)
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _tokenGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionTokenByCredId.TryGetValue(id, out cached) && cached != invalidToken)
            {
                return (cached, null);
            }

            (string? jar, string? loginError) = await EnsureCookieJarAsync(handler, creds, ct);
            if (jar is null)
            {
                return (null, loginError);
            }

            (string? token, string? tokenError) = await GetSessionTokenAsync(handler, jar, ct);
            if (token is null)
            {
                // The cached jar is likely stale too (the `user` cookie expired) — drop the value WE used
                // so the next mint re-logs-in.
                ((ICollection<KeyValuePair<int, string>>)_cookieJarByCredId)
                    .Remove(new KeyValuePair<int, string>(id, jar));
                return (null, tokenError);
            }

            _sessionTokenByCredId[id] = token;
            return (token, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Mints a one-off session token for account verification/refresh — a fresh login (no cookie
    /// or token caching, since the Settings UI passes possibly-unsaved credentials).</summary>
    private async Task<(string? Token, string? Error)> MintFreshTokenAsync(HttpHandler handler, string? username, string? password, CancellationToken ct)
    {
        (string? jar, string? loginError) = await LoginAsync(handler, username, password, ct);
        if (jar is null)
        {
            return (null, loginError);
        }

        return await GetSessionTokenAsync(handler, jar, ct);
    }

    /// <summary>Returns the cached cookie jar for the account, logging in once (gated per credentials id)
    /// on a cache miss.</summary>
    private async Task<(string? Jar, string? Error)> EnsureCookieJarAsync(HttpHandler handler, FileHosterLoginDto creds, CancellationToken ct)
    {
        int id = creds.Id;
        if (_cookieJarByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cookieJarByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? jar, string? error) = await LoginAsync(handler, creds.Username, creds.Password, ct);
            if (jar is null)
            {
                return (null, error);
            }

            _cookieJarByCredId[id] = jar;
            return (jar, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>GET <c>/login/</c> (capture cookies + scrape <c>security</c>) → POST
    /// <c>client_login/mediafire.php</c> forwarding those cookies. Success = the response sets a
    /// <c>user</c> cookie, which is merged into the returned jar.</summary>
    private async Task<(string? Jar, string? Error)> LoginAsync(HttpHandler handler, string? email, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return (null, "MediaFire account needs an email and password.");
        }

        HttpResponseSnapshot loginPage;
        try
        {
            loginPage = await GetSnapshotAsync(handler, LoginPageUrl, headers: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "MediaFire login page fetch failed: " + ex.Message);
        }

        Dictionary<string, string> jar = new(StringComparer.Ordinal);
        MergeCookies(jar, loginPage.SetCookies);

        Match security = _securityTokenRegex.Match(loginPage.Body);
        if (!security.Success)
        {
            return (null, $"MediaFire login page did not contain a security token (HTTP {loginPage.StatusCode}).");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["security"] = security.Groups[1].Value,
            ["login_email"] = email,
            ["login_pass"] = password,
            ["login_remember"] = "yes",
        };
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = CookieHeader(jar),
            ["Origin"] = Host,
            ["Referer"] = LoginPageUrl,
        };

        HttpResponseSnapshot loginResp;
        try
        {
            loginResp = await PostFormAsync(handler, ClientLoginUrl, form, headers, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "MediaFire login request failed: " + ex.Message);
        }

        MergeCookies(jar, loginResp.SetCookies);
        if (jar.ContainsKey("user"))
        {
            return (CookieHeader(jar), null);
        }

        // No `user` cookie → login was rejected. Surface MediaFire's own errorMessage when present.
        string? apiMessage = TryReadLoginError(loginResp.Body);
        return (null, apiMessage is not null
            ? "MediaFire login failed: " + apiMessage
            : $"MediaFire login failed — check the email and password (HTTP {loginResp.StatusCode}).");
    }

    /// <summary>POST <c>/application/get_session_token.php</c> (empty body) with the login cookie jar →
    /// the <c>session_token</c>. Null + a message when MediaFire rejects the session.</summary>
    private async Task<(string? Token, string? Error)> GetSessionTokenAsync(HttpHandler handler, string cookieJar, CancellationToken ct)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = cookieJar,
            ["Origin"] = AppOrigin,
            ["Referer"] = AppOrigin + "/",
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(handler, SessionTokenUrl, new Dictionary<string, string>(StringComparer.Ordinal), headers, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "MediaFire session-token request failed: " + ex.Message);
        }

        if (!TryGetResponse(snap.Body, out JsonElement response))
        {
            return (null, $"MediaFire session-token response was unreadable (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        string? token = Str(response, "session_token");
        if (!string.IsNullOrEmpty(token))
        {
            return (token, null);
        }

        return (null, "MediaFire session sign-in failed: " + (ErrorMessage(response) ?? "no session token returned."));
    }

    // ── Upload steps ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>POST <c>upload/check.php</c> — the hash-dedup + storage pre-flight. The middle flag is
    /// true when the session token expired (caller refreshes it and retries).</summary>
    private async Task<(CheckOutcome? Outcome, bool SessionExpired, string? Error)> CheckAsync(AttemptContext ctx, string sessionToken, string hash)
    {
        string uploads = JsonSerializer.Serialize(new[]
        {
            new UploadCheckEntry
            {
                Filename = ctx.FileName,
                FolderKey = RootFolderKey,
                Size = ctx.FileSize,
                Hash = hash,
                Resumable = "yes",
                Preemptive = "yes",
            },
        });

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["uploads"] = uploads,
            ["response_format"] = "json",
            ["session_token"] = sessionToken,
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(ctx.Handler, CheckUrl, form, ApiHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, false, "MediaFire upload check failed: " + ex.Message);
        }

        if (!TryGetResponse(snap.Body, out JsonElement response))
        {
            return (null, false, $"MediaFire upload check was unreadable (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        if (ErrorMessage(response) is { } err)
        {
            return (null, IsSessionExpired(response), "MediaFire upload check was rejected: " + err);
        }

        string? simpleUrl = null;
        if (response.TryGetProperty("upload_url", out JsonElement uploadUrl) && uploadUrl.ValueKind == JsonValueKind.Object)
        {
            simpleUrl = Str(uploadUrl, "simple") ?? Str(uploadUrl, "simple_fallback");
        }

        return (new CheckOutcome(
            HashExists: string.Equals(Str(response, "hash_exists"), "yes", StringComparison.OrdinalIgnoreCase),
            StorageExceeded: string.Equals(Str(response, "storage_limit_exceeded"), "yes", StringComparison.OrdinalIgnoreCase),
            AvailableSpace: ParseLong(Str(response, "available_space")),
            SimpleUrl: simpleUrl), false, null);
    }

    /// <summary>POST <c>upload/instant.php</c> for a hash that already exists — returns the quickkey. The
    /// middle flag is true when the session token expired (caller refreshes it and retries).</summary>
    private async Task<(string? QuickKey, bool SessionExpired, string? Error)> InstantAsync(AttemptContext ctx, string sessionToken, string hash)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["filename"] = ctx.FileName,
            ["folder_key"] = RootFolderKey,
            ["size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
            ["hash"] = hash,
            ["response_format"] = "json",
            ["session_token"] = sessionToken,
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(ctx.Handler, InstantUrl, form, ApiHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, false, "MediaFire instant upload failed: " + ex.Message);
        }

        if (!TryGetResponse(snap.Body, out JsonElement response))
        {
            return (null, false, $"MediaFire instant upload was unreadable (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        if (ErrorMessage(response) is { } err)
        {
            return (null, IsSessionExpired(response), "MediaFire instant upload was rejected: " + err);
        }

        string? quickKey = Str(response, "quickkey");
        return string.IsNullOrEmpty(quickKey)
            ? (null, false, $"MediaFire instant upload returned no quickkey: {Snippet(snap.Body)}")
            : (quickKey, false, null);
    }

    /// <summary>Streams the raw file bytes to <c>upload/simple.php</c> (POST body =
    /// <c>application/octet-stream</c>; <c>x-filename</c>/<c>x-filesize</c>/<c>x-filehash</c> headers;
    /// <c>session_token</c> in the query).</summary>
    private async Task<HttpResponseSnapshot> SimpleUploadAsync(AttemptContext ctx, string simpleUrl, string sessionToken, string hash)
    {
        string url = simpleUrl
            + (simpleUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + "session_token=" + Uri.EscapeDataString(sessionToken)
            + "&response_format=json";

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["x-filename"] = Uri.EscapeDataString(ctx.FileName),
            ["x-filesize"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
            ["x-filehash"] = hash,
            ["Origin"] = AppOrigin,
            ["Referer"] = AppOrigin + "/",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(url, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadFileBodyAsync(
            HttpMethod.Post,
            ctx.FilePath,
            url,
            contentType: "application/octet-stream",
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>Parses the simple-upload response: <c>doupload.key</c> (poll key), or an already-present
    /// <c>quickkey</c>, or an error. The third flag is true when the session token expired mid-upload
    /// (<c>error 105</c> — MediaFire returns HTTP 200 with the error in the body even for the upload
    /// server, so the caller refreshes the token and re-sends).</summary>
    private static (string? PollKey, string? DirectQuickKey, bool SessionExpired, string? Error) ParseSimpleUpload(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, false, $"MediaFire upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (!TryGetResponse(response.Body, out JsonElement resp))
        {
            return (null, null, false, $"MediaFire upload response was unreadable (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (ErrorMessage(resp) is { } topErr)
        {
            return (null, null, IsSessionExpired(resp), "MediaFire upload was rejected: " + topErr);
        }

        if (!resp.TryGetProperty("doupload", out JsonElement doupload) || doupload.ValueKind != JsonValueKind.Object)
        {
            return (null, null, false, $"MediaFire upload returned no doupload block: {Snippet(response.Body)}");
        }

        // doupload.result "0" = accepted; anything else is a failure (with a message on the doupload).
        string? result = Str(doupload, "result");
        string? fileError = Str(doupload, "fileerror");
        if (!string.IsNullOrEmpty(fileError) && fileError != "0")
        {
            return (null, null, false, "MediaFire upload was rejected: " + (Str(doupload, "description") ?? "error " + fileError));
        }

        string? directQuickKey = Str(doupload, "quickkey");
        string? key = Str(doupload, "key");
        if (!string.IsNullOrEmpty(directQuickKey))
        {
            return (null, directQuickKey, false, null);
        }

        if (!string.IsNullOrEmpty(key))
        {
            return (key, null, false, null);
        }

        return (null, null, false, $"MediaFire upload returned neither a poll key nor a quickkey (result {result}): {Snippet(response.Body)}");
    }

    /// <summary>Polls <c>upload/poll_upload.php</c> until the upload key resolves to a quickkey. The middle
    /// flag is true when the session token expired (caller refreshes it and re-polls).</summary>
    private async Task<(string? QuickKey, bool SessionExpired, string? Error)> PollForQuickKeyAsync(AttemptContext ctx, string sessionToken, string key)
    {
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();

            Dictionary<string, string> form = new(StringComparer.Ordinal)
            {
                ["key"] = key,
                ["response_format"] = "json",
                ["session_token"] = sessionToken,
            };

            HttpResponseSnapshot snap;
            try
            {
                snap = await PostFormAsync(ctx.Handler, PollUrl, form, ApiHeaders(), ctx.Cancellation);
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, false, "MediaFire upload polling failed: " + ex.Message);
            }

            if (TryGetResponse(snap.Body, out JsonElement response))
            {
                if (ErrorMessage(response) is { } err)
                {
                    return (null, IsSessionExpired(response), "MediaFire upload polling was rejected: " + err);
                }

                if (response.TryGetProperty("doupload", out JsonElement doupload) && doupload.ValueKind == JsonValueKind.Object)
                {
                    string? fileError = Str(doupload, "fileerror");
                    if (!string.IsNullOrEmpty(fileError) && fileError != "0")
                    {
                        return (null, false, "MediaFire upload failed while processing: " + (Str(doupload, "description") ?? "error " + fileError));
                    }

                    string? quickKey = Str(doupload, "quickkey");
                    if (!string.IsNullOrEmpty(quickKey))
                    {
                        return (quickKey, false, null);
                    }
                }
            }

            await DelayAsync(PollInterval, ctx.Cancellation);
        }

        return (null, false, "MediaFire did not finish processing the upload in time.");
    }

    /// <summary>POST <c>user/get_info.php</c> → (used, limit, premium). Storage failures collapse to
    /// nulls (best-effort) so a transient hiccup leaves usage blank rather than failing the account
    /// check.</summary>
    private async Task<(long? Used, long? Limit, bool Premium)> TryReadUserInfoAsync(HttpHandler handler, string sessionToken, CancellationToken ct)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["session_token"] = sessionToken,
            ["response_format"] = "json",
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(handler, UserInfoUrl, form, ApiHeaders(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null, false);
        }

        if (!TryGetResponse(snap.Body, out JsonElement response)
            || !response.TryGetProperty("user_info", out JsonElement info)
            || info.ValueKind != JsonValueKind.Object)
        {
            return (null, null, false);
        }

        long? used = ParseLong(Str(info, "used_storage_size"));
        long? limit = ParseLong(Str(info, "storage_limit"));
        bool premium = string.Equals(Str(info, "premium"), "yes", StringComparison.OrdinalIgnoreCase);
        return (used, limit, premium);
    }

    // ── HTTP seams ───────────────────────────────────────────────────────────────────────────────────

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url, headers) : handler.GetSnapshotAsync(url, headers, ct);

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _postFormOverride is not null ? _postFormOverride(url, form, headers) : handler.PostFormAsync(url, form, headers, ct);

    private Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
        => _computeHashOverride is not null ? _computeHashOverride(filePath, ct) : ComputeSha256FromFileAsync(filePath, ct);

    private static async Task<string> ComputeSha256FromFileAsync(string filePath, CancellationToken ct)
    {
        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using SHA256 sha = SHA256.Create();
        byte[] digest = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Overridable delay so poll-loop tests don't wait real seconds.</summary>
    private Task DelayAsync(TimeSpan delay, CancellationToken ct)
        => _postFormOverride is not null ? Task.CompletedTask : Task.Delay(delay, ct);

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ApiHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = AppOrigin,
        ["Referer"] = AppOrigin + "/",
    };

    /// <summary>Merges raw <c>Set-Cookie</c> lines (<c>name=value; attr…</c>) into a name→value jar; an
    /// empty value clears the entry (MediaFire never does this at login, but it keeps the jar honest).</summary>
    private static void MergeCookies(Dictionary<string, string> jar, IReadOnlyList<string> setCookies)
    {
        foreach (string raw in setCookies)
        {
            int eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = raw[..eq].Trim();
            int semi = raw.IndexOf(';', eq);
            string value = (semi < 0 ? raw[(eq + 1)..] : raw[(eq + 1)..semi]).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (value.Length == 0)
            {
                jar.Remove(name);
            }
            else
            {
                jar[name] = value;
            }
        }
    }

    private static string CookieHeader(Dictionary<string, string> jar)
        => string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>Reads MediaFire's login error text. A rejected login returns a JSON envelope with an
    /// <c>errorMessage</c> (e.g. "You have entered an invalid login…" or "This form has been idle…").</summary>
    private static string? TryReadLoginError(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errorMessage", out JsonElement em) && em.ValueKind == JsonValueKind.String)
                {
                    return em.GetString();
                }

                if (root.TryGetProperty("error", out JsonElement er) && er.ValueKind == JsonValueKind.String)
                {
                    return er.GetString();
                }
            }
        }
        catch
        {
            // Not JSON (e.g. an HTML redirect target) — no structured message to surface.
        }

        return null;
    }

    /// <summary>Unwraps the MediaFire <c>{"response":{…}}</c> envelope; the returned element is cloned so
    /// it outlives the JsonDocument. False on non-JSON or a missing <c>response</c>.</summary>
    private static bool TryGetResponse(string body, out JsonElement response)
    {
        response = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("response", out JsonElement resp)
                && resp.ValueKind == JsonValueKind.Object)
            {
                response = resp.Clone();
                return true;
            }
        }
        catch
        {
            // Not JSON.
        }

        return false;
    }

    /// <summary>MediaFire flags failures with <c>"result":"Error"</c> + a <c>message</c>. Returns that
    /// message, or null when the response is a success.</summary>
    private static string? ErrorMessage(JsonElement response)
        => string.Equals(Str(response, "result"), "Error", StringComparison.OrdinalIgnoreCase)
            ? Str(response, "message") ?? "unspecified error"
            : null;

    /// <summary>True when the response is the "session token expired or invalid" error (code
    /// <see cref="SessionExpiredError"/>) — the signal to refresh the shared token and retry. The
    /// <c>error</c> field is a number in most responses but arrives as a string on the upload server.</summary>
    private static bool IsSessionExpired(JsonElement response)
    {
        if (!response.TryGetProperty("error", out JsonElement e))
        {
            return false;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out int n) && n == SessionExpiredError,
            JsonValueKind.String => int.TryParse(e.GetString(), out int s) && s == SessionExpiredError,
            _ => false,
        };
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? ParseLong(string? s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim().Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    private sealed record CheckOutcome(bool HashExists, bool StorageExceeded, long? AvailableSpace, string? SimpleUrl);

    /// <summary>One entry in the <c>upload/check.php</c> <c>uploads</c> JSON array.</summary>
    private sealed class UploadCheckEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("filename")] public string Filename { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("folder_key")] public string FolderKey { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("hash")] public string Hash { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("resumable")] public string Resumable { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("preemptive")] public string Preemptive { get; init; } = string.Empty;
    }

    [GeneratedRegex("<input[^>]*\\bname=\"security\"[^>]*\\bvalue=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
