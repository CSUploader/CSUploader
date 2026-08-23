// <copyright file="HitFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// HitFile (hitfile.net) upload pipeline — supports both anonymous (not-logged-in) and
/// registered-account uploads. Verified end-to-end against the live site 2026-06-15 (a
/// Vue/Vuetify SPA backed by a small JSON API; the classic XFileSharing scrape doesn't apply).
/// <list type="bullet">
///   <item><b>Discover the upload server.</b> POST <c>https://app.hitfile.net/api/upload/urls</c>
///   with the JSON body <c>{"count":1}</c> → <c>{"urls":["https://sNNN.hitfile.net/uploadfile"]}</c>.
///   The storage host (<c>sNNN</c>) is assigned per request, so this is run fresh for each file.
///   Identical for anonymous and registered uploads — no auth/cookies (confirmed by a cookieless probe).</item>
///   <item><b>Upload.</b> Browser-shaped multipart POST to that URL: <c>Filedata</c> (the file),
///   <c>apptype=fd2</c> (the web app's id), <c>folder_id=0</c> (root), and — for a registered
///   account only — <c>user_id=&lt;appId&gt;</c>. The upload itself is cookieless
///   (<c>withCredentials:false</c> in the SPA); the <c>user_id</c> field is the sole link to the
///   account. Response <c>{"result":true,"id":"&lt;code&gt;","message":"Everything is ok"}</c>.</item>
///   <item><b>Result.</b> The share link is <c>https://hitfile.net/&lt;code&gt;</c> (the bare-code
///   URL resolves directly; the named <c>/&lt;code&gt;/&lt;name&gt;.html</c> variant is cosmetic).</item>
/// </list>
/// <para>
/// <b>Registered accounts.</b> Login is Cloudflare-Turnstile-gated, so <see cref="CheckAccountAsync"/>
/// drives a WebView2 sign-in (<see cref="IInteractiveAuthService"/>) to capture the
/// <c>.hitfile.net</c> session cookies, then POSTs <c>/api/user/app/id</c> (cookies, empty body) →
/// <c>{"appId":"&lt;32-hex&gt;"}</c>. That <c>appId</c> is the stable per-account upload token; it is
/// persisted in <see cref="FileHosterLoginDto.ApiKey"/> (the generic secondary-credential slot)
/// and attached as <c>user_id</c> on every upload — auth is purely cookie-based and bearer-free, but
/// the appId (not the cookies) is the durable credential, so uploads never re-open the WebView.
/// The signed-in account's login email rides on that same <c>/api/user/app/id</c> response as a
/// CORS-exposed <c>x-logged-in</c> header (no extra request), and is surfaced as
/// <see cref="AccountCheckResult.DerivedUsername"/> → the account's displayed name.
/// </para>
/// No declared size cap is exposed by the API/SPA, so <see cref="MaxFileSize"/> is null — an
/// oversized file surfaces the server's own <c>result:false</c> rejection rather than a guessed
/// client-side limit that might wrongly block valid uploads.
/// </summary>
public sealed partial class HitFilePipeline : IFileHosterPipeline, ISessionRefreshablePipeline, IStorageRefreshablePipeline
{
    private const string DiscoveryUrl = "https://app.hitfile.net/api/upload/urls";

    // count:1 — one storage server per file; we discover fresh for every upload.
    private const string DiscoveryBody = """{"count":1}""";

    // The web app's identifier the upload endpoint expects; constant for the public site.
    private const string AppType = "fd2";

    private const string SiteOrigin = "https://hitfile.net";
    private const string SiteReferer = "https://hitfile.net/";
    private const string DownloadBase = "https://hitfile.net/";

    // WebView sign-in. The Turnstile login form lives on hitfile.net; the SPA logs in via XHR.
    // HitFile sets NO capturable login marker: kohanasession7 looks identical signed-in vs
    // anonymous and login DELETES the `sid` cookie (Max-Age=0). So instead of keying completion on
    // a cookie, we let the signed-in PAGE fetch the appId itself (the SPA's own /api/user/app/id
    // call, which carries the session — HttpOnly cookies included — automatically) and hand it back.
    // We ALSO capture the cookie jar (CookieCaptureUrl) so "Check / Refresh" can re-read storage
    // from C# later without re-opening the WebView (the HttpOnly fd_session IS available via
    // CoreWebView2.CookieManager — that flag only gates document.cookie, not the manager).
    private const string LoginUrl = "https://hitfile.net/login";
    private const string CookieDomain = ".hitfile.net";
    private const string SessionCookieName = "kohanasession7"; // unused with the probe; the spec requires a name

    // Refresh (C# re-read of storage usage with the captured session). app.hitfile.net is plain
    // nginx/PHP (not Cloudflare-fronted), and the Laravel session isn't IP-bound, so the cookies
    // captured at sign-in work from any proxy. CookieCaptureUrl is the origin whose jar we harvest.
    private const string ApiBase = "https://app.hitfile.net/api";
    private const string AppIdUrl = ApiBase + "/user/app/id";
    private const string FolderContentUrl = ApiBase + "/folder/content";
    private const string CookieCaptureUrl = "https://app.hitfile.net/";

    // JS run in the signed-in page on each poll tick. Does exactly what the SPA does — credentialed
    // fetches that carry the HttpOnly session automatically:
    //   1. POST /api/user/app/id      → the account's appId (returns it only once logged in).
    //   2. POST /api/folder/content   → RECURSIVELY walk the account's folders (folder_id null=root,
    //      then each type==='folder' item's id) summing file sizes for "bytes used". The endpoint
    //      lists one folder's direct children only, so the walk must recurse or files in subfolders
    //      are missed. HitFile exposes no raw byte total; sizes are human strings ("4,98 Mb", binary
    //      units, comma decimal) so the sum is approximate. Accounts are unlimited → no quota.
    // Returns "" until the appId is known, then a JSON string {"appId":...,"usedBytes":N|null}; the
    // WebView completes the moment it returns non-empty. A running total lives on window so the 8 s
    // safety timer can return a partial (rather than null) if a big/deep account is still walking —
    // sign-in never hangs on the storage step. Folder/page caps bound a pathological tree. Inits once.
    private const string AppIdProbeScript = """
        (function () {
          if (!window.__csuHF) {
            window.__csuHF = true;
            window.__csuHFout = '';
            window.__csuHFused = 0;
            window.__csuHFuser = null;
            var API = 'https://app.hitfile.net/api';
            var done = function (appId, used) { window.__csuHFout = JSON.stringify({ appId: appId, usedBytes: used, username: window.__csuHFuser }); };
            var parseSize = function (s) {
              var m = String(s == null ? '' : s).replace(',', '.').match(/([0-9]+(?:\.[0-9]+)?)\s*([KMGTP]?)b/i);
              if (!m) { return 0; }
              var n = parseFloat(m[1]);
              if (!isFinite(n)) { return 0; }
              var mult = { '': 1, K: 1024, M: 1048576, G: 1073741824, T: 1099511627776, P: 1125899906842624 }[(m[2] || '').toUpperCase()] || 1;
              return Math.round(n * mult);
            };
            var walk = function (appId) {
              var queue = [null], folders = 0;
              var nextFolder = function () {
                if (queue.length === 0 || ++folders > 500) { done(appId, window.__csuHFused); return; }
                var fid = queue.shift(), page = 1, per = 200, pages = 0;
                var nextPage = function () {
                  if (++pages > 200) { nextFolder(); return; }
                  fetch(API + '/folder/content', {
                    method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ app_type: 'fd2', folder_id: fid, show_folders: true, filters: '{}', page: page, per_page: per, order_by: 'created_date', order_dir: 'desc', search: '', columns: ['name', 'size'], show_md5_copies: false })
                  })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (d) {
                      if (!d || !d.items) { nextFolder(); return; }
                      for (var i = 0; i < d.items.length; i++) {
                        var it = d.items[i];
                        if (it.type === 'file') { window.__csuHFused += parseSize(it.size); }
                        else if (it.type === 'folder' && it.id != null) { queue.push(it.id); }
                      }
                      if (d.items.length >= per && page * per < (d.total || 0)) { page++; nextPage(); }
                      else { nextFolder(); }
                    })
                    .catch(function () { nextFolder(); });
                };
                nextPage();
              };
              nextFolder();
            };
            var getAppId = function () {
              fetch(API + '/user/app/id', { method: 'POST', credentials: 'include' })
                .then(function (r) {
                  if (!r.ok) { return null; }
                  // The signed-in account's login email rides on this same response as a
                  // CORS-exposed header (Access-Control-Expose-Headers: x-logged-in). Absent
                  // when not authenticated, so we only ever pair it with a real appId below.
                  var u = r.headers.get('x-logged-in');
                  if (u) { window.__csuHFuser = u; }
                  return r.json();
                })
                .then(function (d) {
                  if (d && d.appId) {
                    setTimeout(function () { if (!window.__csuHFout) { done(d.appId, window.__csuHFused); } }, 8000);
                    walk(d.appId);
                  } else { setTimeout(getAppId, 1500); }
                })
                .catch(function () { setTimeout(getAppId, 1500); });
            };
            getAppId();
          }
          return window.__csuHFout;
        })();
        """;

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, string, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, string?, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _cookiePostOverride;

    public HitFilePipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — drives the discovery POST and the multipart upload from canned
    /// responses so the parse logic can be exercised without the network.</summary>
    internal HitFilePipeline(
        Func<string, string, HttpResponseSnapshot> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _postJsonOverride = (url, body) => Task.FromResult(postJsonOverride(url, body));
        _uploadOverride = uploadOverride;
    }

    /// <summary>Test ctor — drives the refresh's cookie-authenticated POSTs (app/id validity check
    /// + folder/content storage walk) from canned responses keyed on (url, body, headers).</summary>
    internal HitFilePipeline(Func<string, string?, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> cookiePostOverride)
    {
        _cookiePostOverride = (url, body, headers) => Task.FromResult(cookiePostOverride(url, body, headers));
    }

    public string Name => "HitFile";

    /// <summary>Free downloads are captcha-gated: its own free-download SPA chunk implements
    /// FreeDownloadCaptchaView with an image captcha (FreePage bundle, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No anonymous per-file cap is advertised by the API; the server enforces its
    /// own limit and an oversized file comes back as a <c>result:false</c> rejection.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>HitFile accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Resolve the account credential up front ===
        // Anonymous (the wizard's synthetic IsAnonymous DTO) omits user_id. A registered account
        // sends user_id=appId, stored in the credential's ApiKey slot by CheckAccountAsync. If an
        // account is selected but never signed in (empty ApiKey), fail before any bytes are sent
        // rather than silently uploading anonymously to the wrong place.
        string? userId = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            if (string.IsNullOrWhiteSpace(ctx.Credentials.ApiKey))
            {
                yield return new AttemptFailed(
                    "HitFile account isn't signed in. Open Settings → Accounts and Sign in to the HitFile account before uploading.",
                    null);
                yield break;
            }

            userId = ctx.Credentials.ApiKey;
        }

        // === Discover a fresh upload node, then upload ===
        (string? uploadUrl, string? discoverError) = await DiscoverUploadServerAsync(ctx);
        if (uploadUrl is null)
        {
            yield return new AttemptFailed(discoverError ?? "HitFile upload server discovery failed", null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel
        // (can't yield from inside the event handler).
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadUrl, userId);

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

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): a mid-send
        // abort or a connect-phase failure to the per-request sNNN node arrives as a safe-to-retry
        // UploadBodyTransferException, and re-running this whole pipeline discovers a FRESH node — the
        // node never double-creates because the body never finished sending. A user cancel surfaces as
        // OperationCanceledException (classified by AttemptRunner). A SERVER VERDICT never throws
        // (UploadMultipartAsync returns the snapshot), so it parses below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Initial sign-in. HitFile's login is Cloudflare-Turnstile-gated, so this drives a WebView2
    /// sign-in; the embedded page fetches the account's <c>appId</c> (its own <c>/api/user/app/id</c>
    /// call, which the page makes with its own logged-in cookie jar) and sums its files for storage
    /// usage, handing both back via <see cref="AccountCheckResult"/>. The Accounts-grid "Check /
    /// Refresh" also lands here, but arrives with the previously-stored appId in
    /// <paramref name="apiKey"/>: the appId is the entire durable upload credential (the upload sends
    /// only <c>user_id=appId</c>, no session), so there's nothing to re-validate offline and refresh
    /// simply confirms the account WITHOUT re-opening a browser. Storage usage is therefore captured
    /// once, at sign-in — re-reading it would need the logged-in session, so re-sign-in to update it.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = handler; // the embedded page fetches the appId itself (AppIdProbeScript); no C# HTTP needed

        string? storedAppId = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        // Already have an appId but reached CheckAccountAsync (not RefreshAccountAsync)? That means
        // no captured session was available — a pasted appId, or an account from before session
        // capture. The appId is the durable upload credential and there's nothing to re-validate
        // offline, so keep the account valid WITHOUT opening a WebView. (Storage is left untouched —
        // null here preserves the last value.) A refresh WITH a saved session takes the C# re-read
        // path in RefreshAccountAsync instead.
        if (storedAppId is not null)
        {
            return new AccountCheckResult(true, AccountType.Free, "HitFile account ready.", ApiKey: storedAppId);
        }

        if (_authService is null)
        {
            // Initial sign-in with no WebView available (unit tests / headless).
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "HitFile sign-in needs the desktop app's embedded browser (to solve the Cloudflare captcha). Alternatively paste your account's upload id.");
        }

        InteractiveAuthSpec spec = new(
            HosterName: Name,
            LoginUrl: LoginUrl,
            CookieDomain: CookieDomain,
            CookieName: SessionCookieName,
            SuccessProbeScript: AppIdProbeScript,
            // Also harvest the logged-in cookie jar so a later "Check / Refresh" can re-read storage
            // from C# (through the proxy) without re-opening this WebView.
            CookieCaptureUrl: CookieCaptureUrl);

        // Only an initial sign-in reaches here (a stored appId returned above), so a failure
        // simply fails — there's no saved appId to fall back on.
        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "HitFile sign-in failed: " + ex.Message);
        }

        (string? appId, long? usedBytes, string? email) = ParseProbeResult(captured?.ProbeValue);
        if (string.IsNullOrEmpty(appId))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "HitFile sign-in was cancelled, or didn't complete before the window was closed.");
        }

        // The page returned the account's appId (the durable upload credential), its login email
        // (DerivedUsername → the account's displayed name), and summed its files for "bytes used".
        // HitFile accounts are unlimited, so quota stays null → the grid shows Used: <n> / Unlimited.
        // captured.SessionCookieValue is the harvested cookie jar (empty if capture failed) — persist
        // it as SessionCookie so refresh can re-read storage server-side.
        string? sessionCookie = string.IsNullOrEmpty(captured?.SessionCookieValue) ? null : captured!.Value.SessionCookieValue;
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to HitFile.",
            ApiKey: appId,
            SessionCookie: sessionCookie,
            DerivedUsername: email,
            StorageUsedBytes: usedBytes,
            StorageQuotaBytes: null);
    }

    /// <summary>
    /// "Check / Refresh" for an account with a saved login session — re-reads storage usage in C#
    /// through the proxy instead of re-opening the WebView. Validates the session via
    /// <c>/api/user/app/id</c> (returns the appId only when authenticated), then walks the account's
    /// folders summing file sizes. An expired session keeps the account valid (the appId is permanent)
    /// and returns no storage so the caller preserves the last-known figure.
    /// </summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // the handler already routes through it; kept for interface parity

        long? usedBytes;
        try
        {
            usedBytes = await ReadStorageViaSessionAsync(sessionCookie, handler, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Transient/transport failure — keep the account, don't touch storage.
            usedBytes = null;
        }

        return usedBytes is { } used
            ? new AccountCheckResult(
                true,
                AccountType.Free,
                "HitFile storage usage refreshed.",
                ApiKey: apiKey,
                StorageUsedBytes: used,
                StorageQuotaBytes: null)
            : new AccountCheckResult(
                true,
                AccountType.Free,
                "HitFile account valid — the saved login session has expired, so storage usage wasn't refreshed (re-sign-in to update it).",
                ApiKey: apiKey);
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard's Summary page: re-reads bytes-used with the
    /// captured <c>.hitfile.net</c> session (the same walk <see cref="RefreshAccountAsync"/> uses) — no
    /// WebView. HitFile accounts are unlimited, so quota is null. Returns null when there's no saved
    /// session or the read fails, so the caller keeps the snapshot.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // the handler already routes through the chosen proxy.

        if (string.IsNullOrEmpty(credentials.SessionCookie))
        {
            return null;
        }

        long? used = await ReadStorageViaSessionAsync(credentials.SessionCookie, handler, ct);
        return used is null ? null : new StorageUsage(used, null);
    }

    /// <summary>
    /// Re-reads the account's total bytes-used with the captured session cookies, mirroring the
    /// JS probe's walk in C#. Returns null when the session is no longer authenticated (so the caller
    /// can distinguish "expired, keep old value" from a genuine 0-byte account). Folder/page caps
    /// bound pathological trees; an unreadable folder is skipped.
    /// </summary>
    private async Task<long?> ReadStorageViaSessionAsync(string sessionCookie, HttpHandler handler, CancellationToken ct)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = sessionCookie,
            ["Origin"] = SiteOrigin,
            ["Referer"] = SiteReferer,
            // Laravel keys some behaviour on these (expectsJson/ajax); the SPA sends them and so does
            // FileBoom's pipeline. Free fidelity — makes the API reliably answer in JSON.
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "application/json",
        };

        // Validity gate: app/id echoes the appId only for an authenticated session; an expired/anon
        // one gets {"appId":null}. Sent body-LESS (null) like the SPA's fetch — no Content-Type, no
        // entity — so a strict body/CSRF validator can't reject it. Null/non-2xx → signal expiry.
        HttpResponseSnapshot idSnap = await CookiePostAsync(AppIdUrl, null, headers, handler, ct);
        if (idSnap.StatusCode is < 200 or >= 300)
        {
            return null;
        }
        (string? probedAppId, _, _) = ParseProbeResult(idSnap.Body);
        if (string.IsNullOrEmpty(probedAppId))
        {
            return null;
        }

        long used = 0;
        Queue<string?> queue = new();
        queue.Enqueue(null); // root (folder_id:null)
        int folders = 0;
        bool rootRead = false;
        while (queue.Count > 0 && folders < 500)
        {
            folders++;
            string? folderToken = queue.Dequeue();
            bool isRoot = folderToken is null;
            int page = 1, pages = 0;
            while (pages < 200)
            {
                pages++;
                string body = BuildFolderContentBody(folderToken, page);
                HttpResponseSnapshot snap = await CookiePostAsync(FolderContentUrl, body, headers, handler, ct);
                if (snap.StatusCode is < 200 or >= 300)
                {
                    break; // skip this folder
                }

                (int count, int total) = AccumulateFolderPage(snap.Body, ref used, queue);
                if (count < 0)
                {
                    break; // unparseable / no items array
                }

                if (isRoot)
                {
                    rootRead = true;
                }

                // Page on only while the folder advertises more rows than we've pulled.
                if (count >= FolderPageSize && page * FolderPageSize < total)
                {
                    page++;
                }
                else
                {
                    break;
                }
            }
        }

        // The app/id gate proves "authenticated", not "the listing was readable". If even the ROOT
        // page never parsed (transient 5xx / unexpected shape), return null so the caller PRESERVES
        // the last-known figure rather than overwriting a real account with a spurious 0. Subfolder
        // failures stay best-effort (skipped), matching the JS probe.
        return rootRead ? used : null;
    }

    private Task<HttpResponseSnapshot> CookiePostAsync(string url, string? body, IReadOnlyDictionary<string, string> headers, HttpHandler handler, CancellationToken ct)
        => _cookiePostOverride is not null
            ? _cookiePostOverride(url, body, headers)
            : handler.PostJsonAsync(url, body, headers, ct);

    private const int FolderPageSize = 200;

    private static string BuildFolderContentBody(string? folderToken, int page)
    {
        // folder_id: "null" for root, else the folder id's RAW JSON token verbatim — a Number stays
        // unquoted (folder_id:123), a String stays quoted (folder_id:"abc"), exactly as the SPA's
        // JSON.stringify(fid) sends it. Re-serializing the parsed value would wrongly quote a numeric
        // id and the strict-typed API would return an empty listing, silently missing the subfolder.
        string folderIdJson = folderToken ?? "null";
        return $$"""
            {"app_type":"fd2","folder_id":{{folderIdJson}},"show_folders":true,"filters":"{}","page":{{page}},"per_page":{{FolderPageSize}},"order_by":"created_date","order_dir":"desc","search":"","columns":["name","size"],"show_md5_copies":false}
            """;
    }

    /// <summary>Parses one folder/content page: adds each file's parsed size to
    /// <paramref name="used"/> and enqueues each subfolder's id. Returns (itemCount, total) for the
    /// pager, or (-1, 0) when the payload has no <c>items</c> array.</summary>
    private static (int Count, int Total) AccumulateFolderPage(string? json, ref long used, Queue<string?> queue)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (-1, 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return (-1, 0);
            }

            int count = 0;
            foreach (JsonElement it in items.EnumerateArray())
            {
                count++;
                string? type = it.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.Equals(type, "file", StringComparison.Ordinal))
                {
                    string? size = it.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    used += ParseHumanSize(size);
                }
                else if (string.Equals(type, "folder", StringComparison.Ordinal) && it.TryGetProperty("id", out JsonElement id))
                {
                    // Keep the id's RAW JSON token so the next request sends folder_id with the SAME
                    // type the SPA does (Number → "123" unquoted, String → "\"abc\"" quoted).
                    string? token = id.ValueKind is JsonValueKind.String or JsonValueKind.Number ? id.GetRawText() : null;
                    if (!string.IsNullOrEmpty(token))
                    {
                        queue.Enqueue(token);
                    }
                }
            }

            return (count, ParseTotal(root, count));
        }
        catch (JsonException)
        {
            return (-1, 0);
        }
    }

    /// <summary>Reads a folder page's advertised <c>total</c> the way the JS probe's
    /// <c>(d.total || 0)</c> coerces it — a JSON number (int or fractional) OR a numeric string —
    /// falling back to the page's own item count when the field is absent/garbage (where it can't
    /// change the pager's decision anyway).</summary>
    private static int ParseTotal(JsonElement root, int fallbackCount)
    {
        if (!root.TryGetProperty("total", out JsonElement tot))
        {
            return fallbackCount;
        }

        return tot.ValueKind switch
        {
            JsonValueKind.Number when tot.TryGetDouble(out double d) && d >= 0 => (int)Math.Min(d, int.MaxValue),
            JsonValueKind.String when int.TryParse(tot.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s) && s >= 0 => s,
            _ => fallbackCount,
        };
    }

    /// <summary>Parses a HitFile human size string (<c>"4,98 Mb"</c> — binary units, comma decimal)
    /// into bytes, mirroring the JS probe's parseSize. Unknown/empty → 0.</summary>
    internal static long ParseHumanSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        Match m = MyRegex().Match(s.Replace(',', '.'));
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            return 0;
        }

        long mult = (m.Groups[2].Value.Length > 0 ? char.ToUpperInvariant(m.Groups[2].Value[0]) : ' ') switch
        {
            'K' => 1024L,
            'M' => 1024L * 1024,
            'G' => 1024L * 1024 * 1024,
            'T' => 1024L * 1024 * 1024 * 1024,
            'P' => 1024L * 1024 * 1024 * 1024 * 1024,
            _ => 1L,
        };

        // AwayFromZero (not the default banker's ToEven) matches JS Math.round's half-up for the
        // non-negative sizes here, so the C# refresh total mirrors the live-verified probe exactly.
        return (long)Math.Round(value * mult, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Parses the probe's JSON payload (<c>{"appId":"…","usedBytes":N|null,"username":"…"|null}</c>)
    /// into the account id, the summed bytes-used (null when the storage walk failed or stalled), and
    /// the signed-in account's login email (read from the <c>x-logged-in</c> response header; null
    /// when not exposed). Returns nulls for a missing/garbage payload so the caller fails the sign-in
    /// cleanly.
    /// </summary>
    internal static (string? AppId, long? UsedBytes, string? Username) ParseProbeResult(string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(probeValue);
            JsonElement root = doc.RootElement;
            string? appId = root.TryGetProperty("appId", out JsonElement a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            long? used = root.TryGetProperty("usedBytes", out JsonElement u)
                && u.ValueKind == JsonValueKind.Number
                && u.TryGetInt64(out long n)
                && n >= 0
                ? n
                : null;
            string? username = root.TryGetProperty("username", out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() is { Length: > 0 } s ? s.Trim() : null
                : null;
            return (appId, used, string.IsNullOrEmpty(username) ? null : username);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// POSTs <c>{"count":1}</c> to the discovery endpoint and returns the first upload URL.
    /// Requires no cookies/headers beyond the JSON content-type (verified live); CORS headers
    /// on the response are advisory to browsers and don't affect a direct client.
    /// </summary>
    private async Task<(string? UploadUrl, string? Error)> DiscoverUploadServerAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = _postJsonOverride is not null
                ? await _postJsonOverride(DiscoveryUrl, DiscoveryBody)
                : await ctx.Handler.PostJsonAsync(DiscoveryUrl, DiscoveryBody, ctx.Cancellation);
        }
        catch (Exception ex)
        {
            return (null, "HitFile upload-server discovery failed: " + ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(snap.Body);
            // Guard the element kind before GetString(): on a type mismatch it throws
            // InvalidOperationException (not JsonException), which would escape the catch
            // below and surface as a raw "pipeline crashed" instead of the clean error.
            if (doc.RootElement.TryGetProperty("urls", out JsonElement urls)
                && urls.ValueKind == JsonValueKind.Array
                && urls.GetArrayLength() > 0
                && urls[0].ValueKind == JsonValueKind.String)
            {
                string? first = urls[0].GetString();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    return (first, null);
                }
            }
        }
        catch (JsonException)
        {
            // fall through to the shared error below
        }

        return (null, $"HitFile did not return an upload URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string? userId)
    {
        // Field order mirrors the browser form intent; PHP/$_POST is order-insensitive, so
        // UploadMultipartAsync appending the file part last (after apptype/folder_id) is fine.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["apptype"] = AppType,
            ["folder_id"] = "0",
        };

        // Registered account: tie the upload to the account. Absent for anonymous uploads.
        if (userId is not null)
        {
            extraFields["user_id"] = userId;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = SiteOrigin,
            ["Referer"] = SiteReferer,
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, uploadUrl, extraFields, headers, ctx.SpeedBudget);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "Filedata",
            ctx.SpeedBudget,
            extraFields: extraFields,
            headers: headers,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is <c>{"result":true,"id":"&lt;code&gt;","message":"..."}</c> → the share link
    /// <c>https://hitfile.net/&lt;code&gt;</c>. A <c>result:false</c> (or missing id) surfaces the
    /// server's <c>message</c> so size/policy rejections are legible.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            // ValueKind-guard every GetString(): a mismatch throws InvalidOperationException
            // (not JsonException), which would otherwise escape the catch below — and here that
            // would turn a 200-accepted upload with an off-shape id into a crash with no link.
            bool ok = root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.True;
            string? id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (ok && !string.IsNullOrWhiteSpace(id))
            {
                return (DownloadBase + id, null);
            }

            string? message = root.TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() : null;
            return (null, $"HitFile upload failed: {message ?? Snippet(response.Body)} (HTTP {response.StatusCode})");
        }
        catch (JsonException)
        {
            return (null, $"HitFile upload returned an unexpected response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
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

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*([KMGTP]?)b", RegexOptions.IgnoreCase, "ja-JP")]
    private static partial Regex MyRegex();
}
