// <copyright file="UfileIoPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// ufile.io anonymous chunked upload pipeline. No account, no captcha — the whole flow was verified live
/// end-to-end 2026-07-04 from a plain client (ufile.io fronts a passive Cloudflare JS-detection challenge,
/// but the upload API works WITHOUT a <c>cf_clearance</c> cookie). It's a CodeIgniter app: a CSRF token
/// rides in the <c>csrf_cookie_name</c> cookie and is echoed back in a <c>csrf_test_name</c> POST field.
/// <list type="number">
///   <item><b>Bootstrap.</b> <c>GET https://ufile.io/</c> sets the <c>csrf_cookie_name</c> (CSRF token) and
///   <c>_ci_sessions_</c> (session id) cookies; both are cached and reused.</item>
///   <item><b>Storage node.</b> <c>POST /v1/upload/select_storage</c> (<c>csrf_test_name</c>) →
///   <c>{"storageBaseUrl":"https://store-…​.ufile.io/","error":""}</c>. The rest of the flow targets that
///   node.</item>
///   <item><b>Session.</b> <c>POST &lt;storageBaseUrl&gt;v1/upload/create_session</c>
///   (<c>csrf_test_name</c> + <c>file_size</c>) → <c>{"fuid":"&lt;hex&gt;"}</c>.</item>
///   <item><b>Chunks.</b> for each 99 MB slice (deliberately just under Cloudflare's 100 MB body limit),
///   <c>POST &lt;storageBaseUrl&gt;/v1/upload/chunk</c> (note the double slash — intentional) as multipart
///   <c>chunk_index</c> (1-based) / <c>fuid</c> / <c>file</c> → <c>"Uploaded successfully."</c>.</item>
///   <item><b>Finalise.</b> <c>POST &lt;storageBaseUrl&gt;v1/upload/finalise</c>
///   (<c>csrf_test_name</c>/<c>fuid</c>/<c>file_name</c>/<c>file_type</c>/<c>total_chunks</c>/<c>session_id</c>)
///   → <c>{"slug":"…","url":"https://ufile.io/…"}</c>; the share link is <c>https://ufile.io/&lt;slug&gt;</c>.</item>
/// </list>
/// No hashing. Chunks stream via <see cref="HttpHandler.PostFileChunkAsync"/> (a mid-send fault is
/// retryable — each attempt makes a fresh session, and nothing is committed until finalise).
/// </summary>
public sealed class UfileIoPipeline : IFileHosterPipeline
{
    private const string Host = "https://ufile.io";
    private const string HomeUrl = Host + "/";
    private const string SelectStorageUrl = Host + "/v1/upload/select_storage";
    private const string PublicUrlPrefix = Host + "/";

    // Fallback storage node the site JS uses when select_storage fails.
    private const string FallbackStorageBaseUrl = "https://up.ufile.io/";

    private const string CsrfCookieName = "csrf_cookie_name";
    private const string SessionCookieName = "_ci_sessions_";

    // Registered-account sign-in: reCAPTCHA-gated /login (WebView), then the account's durable 32-hex
    // api_key is scraped from the /dashboard page and reused for all uploads (x-api-key header).
    private const string LoginUrl = Host + "/login";
    private const string CookieDomain = "ufile.io";

    // Runs in the signed-in WebView on each poll tick; once the /dashboard page carries the account's
    // hidden #api_key (i.e. login + reCAPTCHA succeeded) it returns
    // {"apiKey":"<32hex>","storageUsed":N|null,"storageQuota":N|null,"tier":"free|pro|business"|null} —
    // quota = used + the "space-avail" remaining, tier from the "plan-name" label. Returns "" until then.
    private const string ApiKeyProbeScript = """
        (function () {
          if (!window.__csuUF) {
            window.__csuUF = true;
            window.__csuUFout = '';
            var done = function (o) { window.__csuUFout = JSON.stringify(o); };
            var parseSize = function (s) {
              var m = String(s == null ? '' : s).replace(',', '.').match(/([0-9]+(?:\.[0-9]+)?)\s*(Bytes|B|KB|MB|GB|TB|PB)/i);
              if (!m) { return null; }
              var n = parseFloat(m[1]); if (!isFinite(n)) { return null; }
              var mult = { BYTES: 1, B: 1, KB: 1024, MB: 1048576, GB: 1073741824, TB: 1099511627776, PB: 1125899906842624 }[m[2].toUpperCase()] || 1;
              return Math.round(n * mult);
            };
            var fromText = function (t) {
              var key = t.match(/id=["']api_key["'][^>]*value=["']([a-f0-9]{32})["']/i);
              if (!key) { return null; }
              var usedM = t.match(/class=["']space-used["'][^>]*>([^<]+)</i);
              var availM = t.match(/class=["']space-avail["'][^>]*>([^<]+)</i);
              var planM = t.match(/class=["']plan-name["'][^>]*>\s*(Free|Pro|Business)/i);
              var used = usedM ? parseSize(usedM[1]) : null;
              var avail = availM ? parseSize(availM[1]) : null;
              return {
                apiKey: key[1],
                storageUsed: used,
                storageQuota: (used != null && avail != null) ? used + avail : null,
                tier: planM ? planM[1].toLowerCase() : null,
              };
            };
            var poll = function () {
              fetch('/dashboard', { credentials: 'include' })
                .then(function (r) { return r.ok ? r.text() : ''; })
                .then(function (t) { var o = fromText(t); if (o) { done(o); } else { setTimeout(poll, 1500); } })
                .catch(function () { setTimeout(poll, 1500); });
            };
            poll();
          }
          return window.__csuUFout;
        })();
        """;

    /// <summary>ufile's Dropzone chunk size — deliberately just under Cloudflare's 100 MB body limit so
    /// each chunk POST always gets through.</summary>
    private const long DefaultChunkSize = 99_000_000;

    // Per-tier limits. Free == anonymous for file size. (Business file size is 100 GB; the largest
    // Dropzone-observed anonymous cap was 5000 MiB.)
    private const long FreeMaxFileSizeBytes = 5L * 1024 * 1024 * 1024;        // 5 GB (free + anonymous)
    private const long ProMaxFileSizeBytes = 10L * 1024 * 1024 * 1024;        // 10 GB
    private const long BusinessMaxFileSizeBytes = 100L * 1024 * 1024 * 1024;  // 100 GB
    private const int FreeConcurrentUploads = 10;
    private const int ProConcurrentUploads = 30;
    private const int BusinessConcurrentUploads = 99;

    private readonly long _chunkSize;
    private readonly IInteractiveAuthService? _authService;

    // Anonymous CSRF token + session, shared across uploads (one browser-like session). Gated so a batch
    // fetches them once, not per file. Each upload still makes its OWN fuid, so they stay independent.
    private (string Csrf, string Session)? _cookies;
    private readonly SemaphoreSlim _cookieGate = new(1, 1);

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, string, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    /// <summary><paramref name="authService"/> is only needed to ADD a registered account (the reCAPTCHA
    /// WebView sign-in). Anonymous upload needs none.</summary>
    public UfileIoPipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
        _chunkSize = DefaultChunkSize;
    }

    /// <summary>Test ctor — stubs the GET, the urlencoded POSTs, and the chunk POST (and lets the chunk
    /// size be shrunk) so the whole bootstrap → select → session → chunk loop → finalise orchestration runs
    /// without the network or a real file.</summary>
    internal UfileIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> postFormOverride,
        Func<string, IReadOnlyDictionary<string, string>, string, long, HttpResponseSnapshot> chunkOverride,
        long chunkSize = DefaultChunkSize)
    {
        _getOverride = (url, headers) => Task.FromResult(getOverride(url, headers));
        _postFormOverride = (url, form, headers) => Task.FromResult(postFormOverride(url, form, headers));
        _chunkOverride = (url, fields, name, len) => Task.FromResult(chunkOverride(url, fields, name, len));
        _chunkSize = chunkSize;
    }

    public string Name => "Ufile";

    /// <summary>From its own homepage copy (read 2026-08-12): "As a guest user, your file upload(s)
    /// will be hosted for a maximum of 30 days" - a from-upload maximum. Signing up is pitched as
    /// unlocking "10GB of permanent storage", and "Pro users can store files forever".</summary>
    public FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterUpload(30) : FileRetention.Permanent;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>The most restrictive (free/anonymous) cap; higher tiers get more via
    /// <see cref="MaxFileSizeFor"/>.</summary>
    public long? MaxFileSize => FreeMaxFileSizeBytes;

    /// <summary>Per-file cap by tier: free/anonymous 5 GB, pro 10 GB, business 100 GB.</summary>
    public long? MaxFileSizeFor(FileHosterLoginDto credentials) => credentials.AccountType switch
    {
        AccountType.Business => BusinessMaxFileSizeBytes,
        AccountType.Pro => ProMaxFileSizeBytes,
        _ => FreeMaxFileSizeBytes, // Free / Premium / anonymous
    };

    public int? MaxFilesPerPackage => null;

    /// <summary>Simultaneous uploads by tier: free 10, pro 30, business 99 — the scheduler won't run more
    /// than this many "Ufile" uploads at once for the account.</summary>
    public int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials) => credentials.AccountType switch
    {
        AccountType.Business => BusinessConcurrentUploads,
        AccountType.Pro => ProConcurrentUploads,
        _ => FreeConcurrentUploads,
    };

    /// <summary>ufile.io uploads need no account — the wizard offers it as a built-in "Anonymous" option.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-flight: per-file cap (free/anon 5 GB, pro 10 GB, business 100 GB) ===
        long maxBytes = MaxFileSizeFor(ctx.Credentials) ?? FreeMaxFileSizeBytes;
        if (ctx.FileSize > maxBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds ufile.io's {ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // A registered account uploads with its durable api_key (x-api-key header + a "dashboard" finalise
        // so the file lands in the account); an anonymous upload has none.
        string? apiKey = ctx.Credentials.IsAnonymous || string.IsNullOrWhiteSpace(ctx.Credentials.ApiKey)
            ? null
            : ctx.Credentials.ApiKey.Trim();

        // === Step 1: CSRF token + session cookie (GET /, cached) ===
        (string Csrf, string Session)? cookies;
        string? bootstrapError;
        (cookies, bootstrapError) = await EnsureCookiesAsync(ctx.Handler, ctx.Cancellation);
        if (cookies is null)
        {
            yield return new AttemptFailed(bootstrapError ?? "ufile.io could not be reached.", null);
            yield break;
        }

        string csrf = cookies.Value.Csrf;
        string session = cookies.Value.Session;

        // === Step 2: select storage node ===
        string storageBaseUrl = await SelectStorageAsync(ctx, csrf, session, apiKey);

        // === Step 3: create upload session (fresh per attempt → whole-pipeline retry is safe) ===
        (string? fuid, string? sessionError) = await CreateSessionAsync(ctx, storageBaseUrl, csrf, session, apiKey);
        if (fuid is null)
        {
            yield return new AttemptFailed(sessionError ?? "ufile.io upload session could not be created.", null);
            yield break;
        }

        // === Step 4: chunk loop + finalise (with progress) ===
        yield return new TransferStarted(ctx.FileSize);

        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<(string? Slug, string? Error)> uploadTask = UploadChunksAndFinaliseAsync(ctx, storageBaseUrl, csrf, session, fuid, apiKey);
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

        // A mid-send chunk fault propagates as a retryable UploadBodyTransferException: the shared retry
        // layer re-runs this pipeline, which creates a FRESH session (new fuid) and re-uploads — nothing was
        // finalised, so no double-create.
        (string? slug, string? uploadError) = await uploadTask;
        if (slug is null)
        {
            yield return new AttemptFailed(uploadError ?? "ufile.io upload failed.", null);
            yield break;
        }

        yield return new TransferCompleted(PublicUrlPrefix + slug);
    }

    /// <summary>
    /// Registered sign-in: a reCAPTCHA-gated WebView login whose signed-in <c>/dashboard</c> page yields
    /// the account's durable 32-hex <c>api_key</c> (scraped by <see cref="ApiKeyProbeScript"/>), stored in
    /// the ApiKey slot and reused for every upload. A key already in hand stays valid without a WebView.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password; // the embedded page signs in + the probe scrapes the key; no C# HTTP login
        _ = handler;

        string? stored = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (stored is not null)
        {
            return new AccountCheckResult(true, AccountType.Free, "ufile.io account ready.", ApiKey: stored);
        }

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "ufile.io sign-in needs the desktop app's embedded browser (to solve the reCAPTCHA). You can also just use the built-in Anonymous option.");
        }

        InteractiveAuthSpec spec = new(
            HosterName: Name,
            LoginUrl: LoginUrl,
            CookieDomain: CookieDomain,
            CookieName: SessionCookieName,
            SuccessProbeScript: ApiKeyProbeScript);

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "ufile.io sign-in failed: " + ex.Message);
        }

        (string? key, long? used, long? quota, string? tierName) = ParseProbe(captured?.ProbeValue);
        if (string.IsNullOrEmpty(key))
        {
            return new AccountCheckResult(false, AccountType.Free, "ufile.io sign-in was cancelled, or didn't complete before the window was closed.");
        }

        AccountType tier = TierFromName(tierName);
        return new AccountCheckResult(
            true,
            tier,
            $"Signed in to ufile.io ({tier}).",
            DerivedUsername: username,
            ApiKey: key,
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>Parses the probe payload
    /// <c>{"apiKey":"…","storageUsed":N|null,"storageQuota":N|null,"tier":"free|pro|business"|null}</c>.</summary>
    internal static (string? ApiKey, long? StorageUsed, long? StorageQuota, string? Tier) ParseProbe(string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return (null, null, null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(probeValue);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            string? key = root.TryGetProperty("apiKey", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            string? tier = root.TryGetProperty("tier", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (key, NumOrNull(root, "storageUsed"), NumOrNull(root, "storageQuota"), tier);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static long? NumOrNull(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    /// <summary>Maps the dashboard's plan label to the account tier (defaults to Free when unknown).</summary>
    internal static AccountType TierFromName(string? tierName) => tierName?.Trim().ToLowerInvariant() switch
    {
        "business" => AccountType.Business,
        "pro" => AccountType.Pro,
        _ => AccountType.Free,
    };

    /// <summary>Returns the cached (CSRF token, session id), fetching them once (gated) via <c>GET /</c>.</summary>
    private async Task<((string Csrf, string Session)? Cookies, string? Error)> EnsureCookiesAsync(HttpHandler handler, CancellationToken ct)
    {
        if (_cookies is { } cached)
        {
            return (cached, null);
        }

        await _cookieGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cookies is { } already)
            {
                return (already, null);
            }

            HttpResponseSnapshot snap;
            try
            {
                snap = await GetSnapshotAsync(handler, HomeUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, "ufile.io homepage fetch failed: " + ex.Message);
            }

            string? csrf = ExtractCookieValue(snap.SetCookies, CsrfCookieName);
            string? session = ExtractCookieValue(snap.SetCookies, SessionCookieName);
            if (string.IsNullOrEmpty(csrf) || string.IsNullOrEmpty(session))
            {
                return (null, $"ufile.io did not hand out the upload cookies (HTTP {snap.StatusCode}).");
            }

            _cookies = (csrf, session);
            return (_cookies, null);
        }
        finally
        {
            _cookieGate.Release();
        }
    }

    /// <summary>POST <c>select_storage</c> → the storage node base URL. Falls back to up.ufile.io (what the
    /// site JS does) on any failure.</summary>
    private async Task<string> SelectStorageAsync(AttemptContext ctx, string csrf, string session, string? apiKey)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal) { ["csrf_test_name"] = csrf };
        try
        {
            HttpResponseSnapshot snap = await PostFormAsync(ctx.Handler, SelectStorageUrl, form, ApiHeaders(csrf, session, apiKey), ctx.Cancellation);
            string? url = TryReadStringField(snap.Body, "storageBaseUrl");
            if (!string.IsNullOrEmpty(url))
            {
                return url.EndsWith('/') ? url : url + "/";
            }
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // fall through to the default node
        }

        return FallbackStorageBaseUrl;
    }

    /// <summary>POST <c>create_session</c> → the file upload id (<c>fuid</c>).</summary>
    private async Task<(string? Fuid, string? Error)> CreateSessionAsync(AttemptContext ctx, string storageBaseUrl, string csrf, string session, string? apiKey)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["csrf_test_name"] = csrf,
            ["file_size"] = ctx.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(ctx.Handler, storageBaseUrl + "v1/upload/create_session", form, ApiHeaders(csrf, session, apiKey), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "ufile.io create_session failed: " + ex.Message);
        }

        string? fuid = TryReadStringField(snap.Body, "fuid");
        return string.IsNullOrEmpty(fuid)
            ? (null, $"ufile.io create_session returned no fuid (HTTP {snap.StatusCode}): {Snippet(snap.Body)}")
            : (fuid, null);
    }

    private async Task<(string? Slug, string? Error)> UploadChunksAndFinaliseAsync(AttemptContext ctx, string storageBaseUrl, string csrf, string session, string fuid, string? apiKey)
    {
        // storageBaseUrl ends with '/', so + "/v1/upload/chunk" is the intentional double slash the site
        // JS produces (and the server accepts).
        string chunkUrl = storageBaseUrl + "/v1/upload/chunk";
        DateTime started = DateTime.Now;

        long totalChunks = Math.Max(1, (ctx.FileSize + _chunkSize - 1) / _chunkSize);
        long position = 0;
        int chunkIndex = 1; // ufile's chunk_index is 1-based

        while (position < ctx.FileSize || (ctx.FileSize == 0 && chunkIndex == 1))
        {
            long len = Math.Min(_chunkSize, ctx.FileSize - position);

            // The chunk carries no x-api-key (the fuid is already account-bound via create_session).
            HttpResponseSnapshot chunkResp = await UploadChunkAsync(ctx, chunkUrl, chunkIndex, fuid, position, len, started, csrf, session);
            if (chunkResp.StatusCode is < 200 or >= 300)
            {
                return (null, $"ufile.io chunk {chunkIndex} failed (HTTP {chunkResp.StatusCode}): {Snippet(chunkResp.Body)}");
            }

            position += len;
            chunkIndex++;

            if (ctx.FileSize == 0)
            {
                break; // a zero-byte file is one empty chunk
            }
        }

        // === finalise ===
        // A registered upload lands in the account (dashboard=true + folder_id, x-api-key header);
        // anonymous ties the file to the browser session via session_id.
        Dictionary<string, string> finaliseForm = new(StringComparer.Ordinal)
        {
            ["csrf_test_name"] = csrf,
            ["fuid"] = fuid,
            ["file_name"] = ctx.FileName,
            ["file_type"] = FileType(ctx.FileName),
            ["total_chunks"] = totalChunks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (apiKey is not null)
        {
            finaliseForm["dashboard"] = "true";
            finaliseForm["folder_id"] = "0";
        }
        else
        {
            finaliseForm["session_id"] = session;
        }

        HttpResponseSnapshot finaliseResp;
        try
        {
            finaliseResp = await PostFormAsync(ctx.Handler, storageBaseUrl + "v1/upload/finalise", finaliseForm, ApiHeaders(csrf, session, apiKey), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "ufile.io finalise failed: " + ex.Message);
        }

        string? slug = TryReadStringField(finaliseResp.Body, "slug");
        if (!string.IsNullOrEmpty(slug))
        {
            return (slug, null);
        }

        return (null, $"ufile.io finalise did not return a slug (HTTP {finaliseResp.StatusCode}): {Snippet(finaliseResp.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadChunkAsync(AttemptContext ctx, string chunkUrl, int chunkIndex, string fuid, long basePosition, long chunkLength, DateTime started, string csrf, string session)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["chunk_index"] = chunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fuid"] = fuid,
        };

        if (_chunkOverride is not null)
        {
            return await _chunkOverride(chunkUrl, fields, ctx.FileName, chunkLength);
        }

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        file.Seek(basePosition, SeekOrigin.Begin);
        ChunkSliceStream slice = new(file, chunkLength);

        return await ctx.Handler.PostFileChunkAsync(
            chunkUrl,
            fields,
            fileFieldName: "file",
            fileName: ctx.FileName,
            chunkData: slice,
            chunkLength: chunkLength,
            basePosition: basePosition,
            totalFileSize: ctx.FileSize,
            dateTimeStarted: started,
            ctx.SpeedBudget,
            headers: ApiHeaders(csrf, session),
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>The extension after the last dot, matching the site JS (<c>name.substr(lastIndexOf('.')+1)</c>
    /// — a dotless name yields the whole name).</summary>
    private static string FileType(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : fileName;
    }

    private static Dictionary<string, string> ApiHeaders(string csrf, string session, string? apiKey = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = $"{CsrfCookieName}={csrf}; {SessionCookieName}={session}",
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
        };
        if (apiKey is not null)
        {
            headers["x-api-key"] = apiKey;
        }

        return headers;
    }

    private static string? TryReadStringField(string body, string name)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the value of the named cookie from a response's raw <c>Set-Cookie</c> lines.</summary>
    private static string? ExtractCookieValue(IReadOnlyList<string> setCookies, string name)
    {
        foreach (string raw in setCookies)
        {
            int eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || !string.Equals(raw[..eq].Trim(), name, StringComparison.Ordinal))
            {
                continue;
            }

            int semi = raw.IndexOf(';', eq);
            string value = (semi < 0 ? raw[(eq + 1)..] : raw[(eq + 1)..semi]).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url, null) : handler.GetSnapshotAsync(url, null, ct);

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _postFormOverride is not null ? _postFormOverride(url, form, headers) : handler.PostFormAsync(url, form, headers, ct);

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
}
