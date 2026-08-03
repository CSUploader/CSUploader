// <copyright file="XfsProSessionPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Shared base for XFileSharing hosts running the <b>xfspro</b> chunked plugin in its
/// <b>session</b> form — the variant whose node comes from an <c>op=start_upload</c> POST and whose
/// finalise is <b>form-urlencoded</b>. Two hosts run it: filehoster.io and easybytez.org.
/// <list type="number">
///   <item><b>start_upload.</b> POST the site root (form-urlencoded, with the <c>xfss</c> cookie) →
///   <c>{"url":"https://fsN.HOST/cgi-bin","plugin":"xfspro"}</c>. The <c>url</c> is the CGI base.</item>
///   <item><b>Chunks.</b> PUT ≤100 MiB slices to <c>&lt;url&gt;/put_chunk.cgi</c> as a raw
///   octet-stream carrying a client-minted <c>X-Upload-SID</c>; each answers <c>{"status":"OK"}</c>.</item>
///   <item><b>import_file.</b> POST <c>&lt;url&gt;/api.cgi</c> <b>form-urlencoded</b> with
///   <c>sess_id=&lt;xfss&gt;</c> → <c>{"file_code":…,"links":{"download_link":…},"status":"OK"}</c>.</item>
/// </list>
/// <para>
/// <b>Distinct from <see cref="XfsProAnonymousPipeline"/></b>, which serves the OTHER xfspro variant:
/// a keyless <c>GET /server</c> discovery and a MULTIPART finalise with an empty <c>sess_id</c>. The
/// two differ on exactly those axes; everything between the axes is the same protocol. Do not merge
/// them without a capture showing a host that mixes the axes.
/// </para>
/// <para>
/// <b>Login.</b> Standard XFileSharing web login (GET the login page for the anti-CSRF token → POST
/// <c>op=login</c> → <c>Set-Cookie: xfss</c> on a 302; a wrong password re-renders the page as 200 with
/// no <c>xfss</c>, the reason in an alert box). No WebView and no captcha on either host, so these are
/// plain username/password accounts. The <c>xfss</c> session is cached per credentials id and used two
/// ways: as the <c>Cookie</c> when reading the account page's "Used space", and as
/// <c>import_file</c>'s <c>sess_id</c> to attribute the upload — verified live that <c>sess_id</c>
/// alone suffices, no cookie is needed on the upload requests.
/// </para>
/// <para>
/// <b>Nothing exists server-side until import_file</b>, so a chunk fault is safe for the shared retry
/// layer to re-run: a retry mints a fresh SID and orphans the partial rather than double-creating.
/// </para>
/// </summary>
public abstract class XfsProSessionPipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    private const string PutChunkPath = "/put_chunk.cgi";
    private const string ApiPath = "/api.cgi";

    /// <summary>Site root, no trailing slash (e.g. <c>https://easybytez.org</c>).</summary>
    protected abstract string Host { get; }

    /// <summary>What the host calls itself in error text. Defaults to <see cref="Name"/>; overridden
    /// where the brand's own casing differs from the display name (filehoster.io).</summary>
    protected virtual string DisplayName => Name;

    /// <summary>Both hosts on this base serve the login form and the dashboard at these paths.</summary>
    protected virtual string LoginPagePath => "/login/";

    protected virtual string AccountPath => "/account/";

    /// <summary>Where <c>op=start_upload</c> is posted. The site root on both hosts.</summary>
    protected virtual string StartUploadUrl => Host + "/";

    private string HomeUrl => Host + "/";

    private string LoginPageUrl => Host + LoginPagePath;

    private string AccountUrl => Host + AccountPath;

    // Login form's anti-CSRF token: <input ... name="token" value="<hex>">. The [^>]*? tolerates other
    // attributes between name and value (e.g. an id), staying within the one tag.
    private static readonly Regex _tokenRegex = new(
        """name=["']token["'][^>]*?\bvalue=["']([a-fA-F0-9]+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Login re-render shows the reason in an alert box: <div class="alert alert-danger">Incorrect Login
    // or Password</div>. Surfacing it turns a bare HTTP code into an actionable message.
    private static readonly Regex _loginErrorRegex = new(
        """<div[^>]*\balert-danger\b[^>]*>\s*([^<]+?)\s*</div>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>How the per-file cap is rendered in the "too big" message — match the units the host
    /// publishes, so the message and its site agree.</summary>
    protected virtual ByteBase CapUnits => ByteBase.Decimal;

    /// <summary>Chunk size — 100 MiB, the Cloudflare request-body cap <c>upload-xfspro.js</c> defaults
    /// to. Files larger than this are split into multiple sequential PUTs under one SID.</summary>
    private const long ChunkSizeBytes = 100L * 1024 * 1024;

    // xfss session cached per credentials id; one login at a time per id (a batch of N files against the
    // same account does ONE login, not N). Same shape as Upstore's usid cache.
    private readonly ConcurrentDictionary<int, string> _xfssByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, long, long, long, Action<long, long>, Task<HttpResponseSnapshot>>? _chunkPutOverride;

    protected XfsProSessionPipeline()
    {
    }

    /// <summary>Test ctor (anonymous) — drives the two form POSTs (start_upload, import_file) and the
    /// per-chunk PUT from canned responses so the orchestration runs without the network. The chunk
    /// override is handed the SID, the chunk's base offset/length, and the progress callback.</summary>
    private protected XfsProSessionPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
    {
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _chunkPutOverride = (url, sid, basePos, len, total, progress) => Task.FromResult(chunkPutOverride(url, sid, basePos, len, total, progress));
    }

    /// <summary>Test ctor (account) — also stubs the GETs (login page, /account/) so the login + storage
    /// flow and the logged-in upload (sess_id wiring) can be exercised without the network.</summary>
    private protected XfsProSessionPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
    {
        _getOverride = (url, headers) => Task.FromResult(getOverride(url, headers));
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _chunkPutOverride = (url, sid, basePos, len, total, progress) => Task.FromResult(chunkPutOverride(url, sid, basePos, len, total, progress));
    }

    public abstract string Name { get; }

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public abstract long? MaxFileSize { get; }

    public int? MaxFilesPerPackage => null;

    /// <summary>Both hosts require an account: their guest tiers exist but refuse the upload
    /// ("uploads are not enabled for your account type"). The wizard won't offer "Anonymous".</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // These hosts require an account — anonymous uploads aren't supported (SupportsAnonymousUpload
        // is false, so the wizard won't offer it; this guards a stale/forced anonymous attempt).
        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed($"{DisplayName} needs an account — add one in Settings; it doesn't allow anonymous uploads.", null);
            yield break;
        }

        // === Pre-check: per-file size cap (free registered tier) ===
        if (MaxFileSize is long cap && ctx.FileSize > cap)
        {
            yield return new AttemptFailed(
                $"File exceeds {DisplayName}'s {ByteUnit.FromBytes(cap, CapUnits).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, CapUnits).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0: resolve the xfss session that attributes the upload to the account ===
        // Verified live that sess_id alone attributes the file — no cookie is needed on the upload requests.
        (string? xfss, string? loginError) = await EnsureSessionAsync(ctx);
        if (xfss is null)
        {
            yield return new AttemptFailed(loginError ?? $"{DisplayName} login failed", null);
            yield break;
        }

        string sessId = xfss;

        // A fresh SID per attempt is what makes a retry safe: re-running picks a new SID and the previous
        // partial upload is orphaned, so import_file (the only record-creating step) never double-creates.
        string sid = GenerateSid();

        // === Step 1: start_upload → the CGI base URL ===
        HttpResponseSnapshot? startResponse = null;
        string? startRequestError = null;
        try
        {
            startResponse = await PostFormAsync(ctx.Handler, StartUploadUrl, BuildStartUploadForm(ctx), ctx.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startRequestError = $"{DisplayName} start_upload request failed: " + ex.Message;
        }

        if (startResponse is null)
        {
            yield return new AttemptFailed(startRequestError ?? $"{DisplayName} start_upload request failed", null);
            yield break;
        }

        (string? baseUrl, string? startError) = ParseStartUpload(startResponse);
        if (baseUrl is null)
        {
            yield return new AttemptFailed(startError ?? $"{DisplayName} start_upload returned no upload URL", null);
            yield break;
        }

        // === Step 2: PUT the bytes in ≤100 MiB chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        var stopwatch = Stopwatch.StartNew();
        void Progress(long sent, long total)
        {
            double speed = sent / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progressChannel.Writer.TryWrite(new TransferProgress(sent, total, speed));
        }

        Task<(bool Ok, string? Error)> chunkTask = UploadChunksAsync(ctx, baseUrl, sid, Progress);

        _ = chunkTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        // A chunk transport fault surfaces here as a retryable UploadBodyTransferException (PutChunkAsync
        // reclassifies it) and propagates out of RunAsync so the shared retry layer re-runs against a
        // fresh SID. A server verdict (non-2xx / not-OK chunk) does NOT throw — it comes back as Ok=false.
        (bool ok, string? chunkError) = await chunkTask;
        if (!ok)
        {
            yield return new AttemptFailed(chunkError ?? $"{DisplayName} chunk upload failed", null);
            yield break;
        }

        yield return new TransferProgress(ctx.FileSize, ctx.FileSize, ctx.FileSize / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        // === Step 3: import_file → the share link ===
        HttpResponseSnapshot? importResponse = null;
        string? importRequestError = null;
        try
        {
            importResponse = await PostFormAsync(ctx.Handler, baseUrl + ApiPath, BuildImportFileForm(ctx, sid, sessId), ctx.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            importRequestError = $"{DisplayName} import_file request failed: " + ex.Message;
        }

        if (importResponse is null)
        {
            yield return new AttemptFailed(importRequestError ?? $"{DisplayName} import_file request failed", null);
            yield break;
        }

        (string? shareUrl, string? importError) = ParseImportFile(importResponse);
        if (shareUrl is null)
        {
            // A rejected account import may mean a stale xfss (the cached session expired) — drop the one
            // WE used (value-matching) so the next attempt re-logs-in. Anonymous has nothing to invalidate.
            if (sessId.Length > 0)
            {
                ((ICollection<KeyValuePair<int, string>>)_xfssByCredId)
                    .Remove(new KeyValuePair<int, string>(ctx.Credentials.Id, sessId));
            }

            yield return new AttemptFailed(importError ?? $"{DisplayName} import_file returned no link", null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl);
    }

    /// <summary>
    /// Validates an account by logging in (<c>op=login</c> → a <c>Set-Cookie: xfss</c>),
    /// then reads "Used space" off <c>/account/</c> (best-effort; no quota is shown, so Available stays
    /// Unlimited). Accounts are reported as free-tier — neither host distinguishes premium here.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? xfss, string? error) = await LoginAsync(handler, username, password, ct);
        if (xfss is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? $"{DisplayName} login failed.");
        }

        long? used = await TryReadUsedSpaceAsync(handler, xfss, ct);
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in (Free)",
            DerivedUsername: username,
            StorageUsedBytes: used,
            StorageQuotaBytes: null); // no quota shown → Available is Unlimited
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard's Summary page: a fresh credential login plus the
    /// same <c>/account/</c> "Used space" read. Returns null on any failure (bad/expired creds, transport)
    /// so the caller keeps the last-known snapshot.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy;

        (string? xfss, _) = await LoginAsync(handler, credentials.Username, credentials.Password, ct);
        if (xfss is null)
        {
            return null;
        }

        long? used = await TryReadUsedSpaceAsync(handler, xfss, ct);
        return used is null ? null : new StorageUsage(used, null);
    }

    /// <summary>Returns the cached xfss for the account, logging in once (gated per credentials id) on a
    /// cache miss. Null + an error message when the login fails.</summary>
    private async Task<(string? Xfss, string? Error)> EnsureSessionAsync(AttemptContext ctx)
    {
        int id = ctx.Credentials.Id;
        if (_xfssByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_xfssByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? xfss, string? error) = await LoginAsync(ctx.Handler, ctx.Credentials.Username, ctx.Credentials.Password, ctx.Cancellation);
            if (xfss is null)
            {
                return (null, error);
            }

            _xfssByCredId[id] = xfss;
            return (xfss, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>XFileSharing web login: GET <c>/login/</c> for the anti-CSRF token, then POST
    /// <c>op=login</c>. Success sets <c>Set-Cookie: xfss</c> on the 302 (the runner's handler doesn't
    /// follow redirects, so it's captured); a wrong password re-renders the page as 200 with no xfss.</summary>
    private async Task<(string? Xfss, string? Error)> LoginAsync(HttpHandler handler, string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, $"{DisplayName} account needs a username/email and password.");
        }

        string token = string.Empty;
        try
        {
            HttpResponseSnapshot page = await GetAsync(handler, LoginPageUrl, null, ct);
            Match m = _tokenRegex.Match(page.Body);
            if (m.Success)
            {
                token = m.Groups[1].Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{DisplayName} login page fetch failed: " + ex.Message);
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "login",
            ["login"] = username,
            ["password"] = password,
            ["token"] = token,
            ["rand"] = string.Empty,
            ["redirect"] = AccountUrl,
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(handler, HomeUrl, form, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{DisplayName} login request failed: " + ex.Message);
        }

        string? xfss = ExtractCookieValue(snap.SetCookies, "xfss");
        if (xfss is not null)
        {
            return (xfss, null);
        }

        // Surface the server's own reason (XFS renders it in an alert box) so a wrong password vs a
        // rate-limit vs a WAF page is obvious, rather than a bare HTTP code.
        string? reason = ExtractLoginError(snap.Body);
        return (null, reason is not null
            ? $"{DisplayName} login failed: {reason}"
            : $"{DisplayName} login failed — check the username and password (HTTP {snap.StatusCode}).");
    }

    /// <summary>Pulls the XFS login error out of the re-rendered login page's alert box (e.g. "Incorrect
    /// Login or Password"). Null when no alert is present.</summary>
    private static string? ExtractLoginError(string body)
    {
        Match m = _loginErrorRegex.Match(body);
        if (!m.Success)
        {
            return null;
        }

        string text = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>GETs the logged-in <c>/account/</c> page (auth = the xfss cookie) and scrapes the rounded
    /// "Used space" GiB figure. Returns null on any failure so a transient hiccup leaves Used blank.</summary>
    private async Task<long?> TryReadUsedSpaceAsync(HttpHandler handler, string xfss, CancellationToken ct)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["Cookie"] = "xfss=" + xfss };
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetAsync(handler, AccountUrl, headers, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return ParseUsedSpaceBytes(snap.Body);
    }

    /// <summary>Parses the dashboard's "Used space" figure into bytes — per-host, because the two
    /// themes render it differently. Null when the panel is absent or the number doesn't parse.</summary>
    protected abstract long? ParseUsedSpaceBytes(string html);

    /// <summary>Reads the value of the named cookie from a response's raw <c>Set-Cookie</c> lines
    /// (<c>name=value; attr=…</c>). Null when absent or empty.</summary>
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

    /// <summary>Mints a 16-digit decimal upload SID, matching <c>upload-xfspro.js</c>
    /// (<c>Array(16).fill().map(() =&gt; Math.floor(Math.random()*10)).join("")</c>).</summary>
    private static string GenerateSid()
    {
        char[] digits = new char[16];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + Random.Shared.Next(10));
        }

        return new string(digits);
    }

    /// <summary>Uploads the file as one or more ≤100 MiB chunks, in order, under <paramref name="sid"/>.
    /// Returns (false, error) on a server verdict; throws (retryable) on a chunk transport fault.</summary>
    private async Task<(bool Ok, string? Error)> UploadChunksAsync(AttemptContext ctx, string baseUrl, string sid, Action<long, long> progress)
    {
        long total = ctx.FileSize;
        int chunkCount = (int)Math.Max(1, (total + ChunkSizeBytes - 1) / ChunkSizeBytes);
        string chunkUrl = baseUrl + PutChunkPath;

        if (_chunkPutOverride is not null)
        {
            for (int i = 0; i < chunkCount; i++)
            {
                long basePos = i * ChunkSizeBytes;
                long len = Math.Min(ChunkSizeBytes, total - basePos);
                HttpResponseSnapshot resp = await _chunkPutOverride(chunkUrl, sid, basePos, len, total, progress);
                (bool ok, string? error) = CheckChunkResponse(resp, i);
                if (!ok)
                {
                    return (false, error);
                }
            }

            return (true, null);
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["X-Upload-SID"] = sid };
        DateTime started = DateTime.Now;
        void OnProgress(object? _, OperationProgressEventArgs e) => progress(e.BytesProcessed, e.Size);
        ctx.Handler.UploadProgress += OnProgress;
        try
        {
            await using FileStream fs = new(ctx.FilePath, FileMode.Open, FileAccess.Read);
            for (int i = 0; i < chunkCount; i++)
            {
                long basePos = i * ChunkSizeBytes;
                long len = Math.Min(ChunkSizeBytes, total - basePos);

                // ChunkSliceStream serves exactly `len` bytes from the shared FileStream (whose position
                // advances as each slice is consumed) and never disposes it — the FileStream lives across
                // all chunks. The PUT's content disposes the slice; that's a no-op for the inner stream.
                ChunkSliceStream slice = new(fs, len);
                HttpResponseSnapshot resp = await ctx.Handler.PutChunkAsync(
                    chunkUrl, slice, len, basePos, total, started, headers, ctx.SpeedLimitProvider, ctx.Cancellation);
                (bool ok, string? error) = CheckChunkResponse(resp, i);
                if (!ok)
                {
                    return (false, error);
                }
            }

            return (true, null);
        }
        finally
        {
            ctx.Handler.UploadProgress -= OnProgress;
        }
    }

    /// <summary>A chunk PUT succeeds with HTTP 2xx and a body echoing <c>"status":"OK"</c>.</summary>
    private (bool Ok, string? Error) CheckChunkResponse(HttpResponseSnapshot resp, int index)
    {
        if (resp.StatusCode is < 200 or >= 300)
        {
            return (false, $"{DisplayName} chunk {index} failed (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
        }

        // Success is {"status":"OK"}. Parse and check the status field specifically (mirrors
        // ParseStartUpload/ParseImportFile) so an error envelope that merely carries the token "OK" in
        // some other field — e.g. {"status":"error","note":"OK"} — can't be read as success.
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.TryGetProperty("status", out JsonElement status)
                && string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (false, $"{DisplayName} chunk {index} was not accepted: {Snippet(resp.Body)}");
    }

    private (string? BaseUrl, string? Error) ParseStartUpload(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"{DisplayName} start_upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("url", out JsonElement url) && url.GetString() is { Length: > 0 } baseUrl)
            {
                return (baseUrl.TrimEnd('/'), null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (null, $"{DisplayName} start_upload returned no upload URL: {Snippet(response.Body)}");
    }

    private (string? ShareUrl, string? Error) ParseImportFile(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"{DisplayName} import_file failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("links", out JsonElement links)
                && links.TryGetProperty("download_link", out JsonElement dl)
                && dl.GetString() is { Length: > 0 } link)
            {
                return (link, null);
            }

            // Fallback: synthesise the link from the file code if the links block is absent.
            if (root.TryGetProperty("file_code", out JsonElement fc) && fc.GetString() is { Length: > 0 } code)
            {
                return ($"{Host}/{code}", null);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error
        }

        return (null, $"{DisplayName} import_file returned no link: {Snippet(response.Body)}");
    }

    /// <summary>
    /// The <c>op=start_upload</c> form. Overridable because the two hosts' captures differ: filehoster.io
    /// sends <c>file_size</c> and <c>file_public=1</c>, easybytez sends neither the size nor a public
    /// flag of 1. This parser is field-presence sensitive (see <c>brupload-multipart-quirks</c>), so each
    /// host sends the set proven against it rather than a near-miss.
    /// </summary>
    protected virtual Dictionary<string, string> BuildStartUploadForm(AttemptContext ctx) => new(StringComparer.Ordinal)
    {
        ["op"] = "start_upload",
        ["file_name"] = ctx.FileName,
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["file_size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The <c>op=import_file</c> form — see <see cref="BuildStartUploadForm"/> for why it is
    /// overridable.</summary>
    protected virtual Dictionary<string, string> BuildImportFileForm(AttemptContext ctx, string sid, string sessId) => new(StringComparer.Ordinal)
    {
        ["op"] = "import_file",
        ["sid"] = sid,
        ["fname"] = ctx.FileName,
        ["sess_id"] = sessId, // the account's xfss — attributes the upload to the account
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
    };

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, CancellationToken ct)
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : handler.PostFormAsync(url, form, ct);

    private Task<HttpResponseSnapshot> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null
            ? _getOverride(url, headers)
            : handler.GetSnapshotAsync(url, headers, ct);

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
}
