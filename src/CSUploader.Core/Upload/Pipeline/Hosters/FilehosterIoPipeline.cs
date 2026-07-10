// <copyright file="FilehosterIoPipeline.cs" company="CSUploader">
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
/// filehoster.io upload pipeline — an XFileSharing host running the "xfspro" chunked-upload plugin.
/// Uploads REQUIRE an account: anonymous uploads aren't offered (the 10 GB per-file cap is the free
/// REGISTERED tier, not an anonymous allowance). Per file (after the xfss session is resolved):
/// <list type="number">
///   <item><b>SID.</b> The client mints a 16-digit random upload SID (matches
///   <c>upload-xfspro.js</c>).</item>
///   <item><b>start_upload.</b> POST <c>https://filehoster.io/</c> (form-urlencoded
///   <c>op=start_upload&amp;file_name&amp;file_descr=&amp;file_public=1&amp;file_size</c>) →
///   <c>{"url":"https://filehoster.io/cgi-bin","plugin":"xfspro"}</c>. The <c>url</c> is the CGI base.</item>
///   <item><b>Chunks.</b> Split the file into ≤100 MiB chunks (the Cloudflare request-body cap the JS
///   targets) and PUT each, in order, to <c>&lt;url&gt;/put_chunk.cgi</c> as a raw
///   <c>application/octet-stream</c> body carrying an <c>X-Upload-SID</c> header. Each replies
///   <c>{"status":"OK"}</c>; the server appends by SID (no offset/range is sent).</item>
///   <item><b>import_file.</b> POST <c>&lt;url&gt;/api.cgi</c> (form-urlencoded
///   <c>op=import_file&amp;sid&amp;fname&amp;sess_id=&lt;xfss&gt;&amp;…</c>) →
///   <c>{"file_code":"…","links":{"download_link":"…"}}</c>. The share link is
///   <c>links.download_link</c> (<c>https://filehoster.io/&lt;file_code&gt;/&lt;name&gt;.html</c>).</item>
/// </list>
/// No hashing. Free-tier cap is 10 GB. <c>import_file</c> is the only record-creating step — a failed
/// chunk before it leaves only orphaned temp data, so a mid-send abort is safe to retry the whole
/// pipeline (a fresh SID discards the partial; see <see cref="HttpHandler.PutChunkAsync"/>).
/// <para><b>Login.</b> Standard XFileSharing login (GET <c>/login/</c> for the token → POST
/// <c>op=login</c> → <c>Set-Cookie: xfss</c> on a 302; a wrong password re-renders the page as 200 with
/// no <c>xfss</c>, the reason in an alert box). The <c>xfss</c> session is cached per credentials id and
/// used two ways: as the <c>Cookie</c> when reading the <c>/account/</c> "Used space" panel (a GiB
/// figure; no quota is shown, so Available is Unlimited), and as <c>import_file</c>'s <c>sess_id</c> to
/// attribute the upload to the account — verified live that <c>sess_id</c> alone suffices, no cookie is
/// needed on the upload requests.</para>
/// </summary>
public sealed class FilehosterIoPipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    private const string Host = "https://filehoster.io";
    private const string HomeUrl = Host + "/";
    private const string LoginPageUrl = Host + "/login/";
    private const string AccountUrl = Host + "/account/";
    private const string PutChunkPath = "/put_chunk.cgi";
    private const string ApiPath = "/api.cgi";

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

    // /account/ dashboard: <div ...>Used space</div> <div class="fs-4 ...">0.06</div> — a GiB figure.
    // Anchored on the value div's distinctive fs-4 class so the page's two "Used space" occurrences can't
    // confuse it (the decoy's following div has no fs-4), and tolerant of an inner span/icon before the
    // number.
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s*space\s*</div>\s*<div[^>]*\bfs-4\b[^>]*>\s*(?:<[^>]+>\s*)*([0-9]+(?:\.[0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Free-tier per-file cap — 10 GB ("Max file size: 10GB" on the upload page; this is the
    /// free REGISTERED allowance, not an anonymous one). The server is the real gate; this fails fast on
    /// an obviously-too-big file. Decimal GB to match the page.</summary>
    private const long FreeMaxFileSizeBytes = 10L * 1000 * 1000 * 1000;

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

    public FilehosterIoPipeline()
    {
    }

    /// <summary>Test ctor (anonymous) — drives the two form POSTs (start_upload, import_file) and the
    /// per-chunk PUT from canned responses so the orchestration runs without the network. The chunk
    /// override is handed the SID, the chunk's base offset/length, and the progress callback.</summary>
    internal FilehosterIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
    {
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _chunkPutOverride = (url, sid, basePos, len, total, progress) => Task.FromResult(chunkPutOverride(url, sid, basePos, len, total, progress));
    }

    /// <summary>Test ctor (account) — also stubs the GETs (login page, /account/) so the login + storage
    /// flow and the logged-in upload (sess_id wiring) can be exercised without the network.</summary>
    internal FilehosterIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
    {
        _getOverride = (url, headers) => Task.FromResult(getOverride(url, headers));
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _chunkPutOverride = (url, sid, basePos, len, total, progress) => Task.FromResult(chunkPutOverride(url, sid, basePos, len, total, progress));
    }

    public string Name => "Filehoster.io";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => FreeMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>filehoster.io requires an account to upload — anonymous uploads aren't offered (the 10 GB
    /// cap is the free registered tier). The wizard won't show the built-in "Anonymous" option for it.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // filehoster.io requires an account — anonymous uploads aren't supported (SupportsAnonymousUpload
        // is false, so the wizard won't offer it; this guards a stale/forced anonymous attempt).
        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed("filehoster.io needs an account — add one in Settings; it doesn't allow anonymous uploads.", null);
            yield break;
        }

        // === Pre-check: per-file size cap (free registered tier) ===
        if (ctx.FileSize > FreeMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds filehoster.io's {ByteUnit.FromBytes(FreeMaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0: resolve the xfss session that attributes the upload to the account ===
        // Verified live that sess_id alone attributes the file — no cookie is needed on the upload requests.
        (string? xfss, string? loginError) = await EnsureSessionAsync(ctx);
        if (xfss is null)
        {
            yield return new AttemptFailed(loginError ?? "filehoster.io login failed", null);
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
            startResponse = await PostFormAsync(ctx.Handler, HomeUrl, BuildStartUploadForm(ctx), ctx.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startRequestError = "filehoster.io start_upload request failed: " + ex.Message;
        }

        if (startResponse is null)
        {
            yield return new AttemptFailed(startRequestError ?? "filehoster.io start_upload request failed", null);
            yield break;
        }

        (string? baseUrl, string? startError) = ParseStartUpload(startResponse);
        if (baseUrl is null)
        {
            yield return new AttemptFailed(startError ?? "filehoster.io start_upload returned no upload URL", null);
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
            yield return new AttemptFailed(chunkError ?? "filehoster.io chunk upload failed", null);
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
            importRequestError = "filehoster.io import_file request failed: " + ex.Message;
        }

        if (importResponse is null)
        {
            yield return new AttemptFailed(importRequestError ?? "filehoster.io import_file request failed", null);
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

            yield return new AttemptFailed(importError ?? "filehoster.io import_file returned no link", null);
            yield break;
        }

        yield return new TransferCompleted(shareUrl);
    }

    /// <summary>
    /// Validates a filehoster.io account by logging in (<c>op=login</c> → a <c>Set-Cookie: xfss</c>),
    /// then reads "Used space" off <c>/account/</c> (best-effort; no quota is shown, so Available stays
    /// Unlimited). filehoster.io accounts are free-tier here — premium isn't distinguished.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? xfss, string? error) = await LoginAsync(handler, username, password, ct);
        if (xfss is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "filehoster.io login failed.");
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
            return (null, "filehoster.io account needs a username/email and password.");
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
            return (null, "filehoster.io login page fetch failed: " + ex.Message);
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
            return (null, "filehoster.io login request failed: " + ex.Message);
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
            ? $"filehoster.io login failed: {reason}"
            : $"filehoster.io login failed — check the username and password (HTTP {snap.StatusCode}).");
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

        return ParseUsedSpace(snap.Body);
    }

    /// <summary>Parses the dashboard's "Used space" value (a GiB figure, ceil-rounded to 2 decimals) into
    /// bytes. Null when the panel is absent or the number doesn't parse.</summary>
    internal static long? ParseUsedSpace(string html)
    {
        Match m = _usedSpaceRegex.Match(html);
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gib)
            || gib < 0)
        {
            return null;
        }

        return (long)(gib * (1L << 30));
    }

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
    private static (bool Ok, string? Error) CheckChunkResponse(HttpResponseSnapshot resp, int index)
    {
        if (resp.StatusCode is < 200 or >= 300)
        {
            return (false, $"filehoster.io chunk {index} failed (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
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

        return (false, $"filehoster.io chunk {index} was not accepted: {Snippet(resp.Body)}");
    }

    private static (string? BaseUrl, string? Error) ParseStartUpload(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"filehoster.io start_upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
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

        return (null, $"filehoster.io start_upload returned no upload URL: {Snippet(response.Body)}");
    }

    private static (string? ShareUrl, string? Error) ParseImportFile(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"filehoster.io import_file failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
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

        return (null, $"filehoster.io import_file returned no link: {Snippet(response.Body)}");
    }

    private static Dictionary<string, string> BuildStartUploadForm(AttemptContext ctx) => new(StringComparer.Ordinal)
    {
        ["op"] = "start_upload",
        ["file_name"] = ctx.FileName,
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["file_size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
    };

    private static Dictionary<string, string> BuildImportFileForm(AttemptContext ctx, string sid, string sessId) => new(StringComparer.Ordinal)
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
