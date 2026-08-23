// <copyright file="DataNodesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DataNodes (datanodes.to) — XFileSharing on the <b>xfspro</b> chunked plugin, anonymous or signed
/// in, <b>3 GiB</b>:
/// <list type="number">
///   <item><b>Node.</b> <c>POST /</c> form-urlencoded <c>op=start_upload</c> with <c>file_name</c>,
///   <c>file_descr</c>, <c>file_public</c> and <c>file_size</c> →
///   <c>{"plugin":"xfspro","url":"https://nodeNN.datanodes.to/cgi-bin"}</c>.</item>
///   <item><b>Chunks.</b> <c>PUT &lt;node&gt;/put_chunk_mt.cgi</c> as a raw octet-stream carrying a
///   client-minted <c>X-Upload-SID</c> and <b><c>X-Seek-To</c></b>, the chunk's byte offset. Each
///   answers <c>{"status":"OK"}</c>.</item>
///   <item><b>Finalise.</b> <c>POST &lt;node&gt;/api.cgi</c> <b>form-urlencoded</b> with
///   <c>op=import_file</c>, <c>sid</c>, <c>fname</c>, <c>sess_id</c>, <c>file_public</c>,
///   <c>link_rcpt</c>, <c>link_pass</c> and <c>to_folder</c> → <c>links.download_link</c> plus a
///   <c>delete_link</c> carrying a killcode.</item>
/// </list>
/// <para>
/// <b>This is a THIRD combination of the xfspro axes</b>, which is why it isn't a shim on either
/// existing base. <see cref="XfsProAnonymousPipeline"/> is <c>GET /server</c> + a MULTIPART finalise
/// with an empty <c>sess_id</c>; <see cref="XfsProSessionPipeline"/> is <c>op=start_upload</c> + an
/// URLENCODED finalise with a real one. DataNodes takes start_upload and the urlencoded finalise from
/// the second, allows the empty <c>sess_id</c> of the first — <b>so one code path serves both
/// anonymous and signed-in uploads, differing only in that field</b> — and adds an axis neither has:
/// <c>put_chunk_<b>mt</b>.cgi</c> with an explicit offset per chunk. Extract a shared base if a
/// second host on this shape turns up; one host does not justify reworking two shipped ones.
/// </para>
/// <para>
/// <b>No cookie is involved anywhere.</b> The site sets none — not even for a visitor — and the
/// whole chain works from a cold client with no page visit first, verified. An early probe here
/// 500'd at the finalise and it was tempting to conclude a visitor session was required; repeating
/// the run cold twice showed it succeeding, so <b>that 500 was transient</b>. Worth knowing: this
/// host can fail the finalise spuriously, after every byte is already up.
/// </para>
/// <para>
/// Its own uploader sends 1 MiB chunks and up to ten at once. This sends <b>8 MiB</b> sequentially —
/// verified — because at 1 MiB a 3 GiB file would be three thousand round trips.
/// </para>
/// </summary>
public sealed class DataNodesPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://datanodes.to";
    private const string LoginPageUrl = Host + "/login";
    private const string AccountPageUrl = Host + "/account";

    /// <summary>The upload page's own <c>:max-size="3221225472"</c>.</summary>
    private const long MaxFileSizeBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>8 MiB. Its own uploader uses 1 MiB, which is far too many requests for this app's files.</summary>
    private const int ChunkSizeBytes = 8 * 1024 * 1024;

    /// <summary>What the login 302 issues the <c>xfss</c> cookie for: <c>Max-Age=2592000</c>.</summary>
    private const int SessionLifetimeDays = 30;

    private static readonly Regex LoginTokenRegex = new(
        """name="token"[^>]*\svalue="([^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The account page's used/quota tile: <c>0.01 <span …>/ 1024 GB</span></c>. The used
    /// figure carries no unit of its own — it is rendered in the quota's, to two decimals.</summary>
    private static readonly Regex StorageRegex = new(
        """Used space.*?>\s*([\d.,]+)\s*<span[^>]*>\s*/\s*([\d.,]+)\s*([KMGT]?B)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The plan chip beside the account name — "Free plan" on the account probed.</summary>
    private static readonly Regex PlanRegex = new(
        """<span[^>]*>\s*(Free|Premium)\s+plan\s*</span>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>>? _chunkOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _getOverride;

    public DataNodesPipeline()
    {
    }

    /// <summary>Test ctor — stubs the form POSTs, the chunk PUTs and the page GETs.</summary>
    internal DataNodesPipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>> chunkOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? getOverride = null)
    {
        _postFormOverride = postFormOverride;
        _chunkOverride = chunkOverride;
        _getOverride = getOverride;
    }

    public string Name => "DataNodes";

    /// <summary>Free downloads are captcha-gated: its live free download component embeds an
    /// interactive Turnstile widget and premium sells no-ads-or-captchas
    /// (2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Verified by uploading real bytes from a cold client with no account and no cookies.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds DataNodes' {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // The ONLY difference between an anonymous and a signed-in upload: this field. Empty uploads
        // as a guest; the account's xfss uploads under it.
        string sessionId = string.Empty;
        if (!ctx.Credentials.IsAnonymous)
        {
            (string? session, string? authError) = await ResolveSessionAsync(ctx);
            if (session is null)
            {
                yield return new AttemptFailed(
                    authError ?? "DataNodes has no sign-in for this account, and uploading without one would "
                    + "file the upload under no account. Re-check the account in Account Manager.",
                    null);
                yield break;
            }

            sessionId = session;
        }

        // === 1. which node ===
        (string? node, string? nodeError) = await StartUploadAsync(ctx, sessionId);
        if (node is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        // === 2. the chunks ===
        yield return new TransferStarted(ctx.FileSize);

        string sid = NewUploadSid();
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<string?> chunkTask = SendChunksAsync(ctx, node, sid, sessionId);
        _ = chunkTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        if (await chunkTask is { } chunkError)
        {
            yield return new AttemptFailed(chunkError, null);
            yield break;
        }

        // === 3. finalise ===
        (string? link, string? deleteLink, string? finaliseError) = await ImportFileAsync(ctx, node, sid, sessionId);
        if (link is null)
        {
            yield return new AttemptFailed(finaliseError!, null);
            yield break;
        }

        // The killcode link is the only handle an anonymous upload has for removing the file, so it
        // is logged rather than dropped — as upload.ee's and GigaFile's are.
        if (deleteLink is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} can be deleted at {deleteLink}");
        }

        yield return new TransferCompleted(link);
    }

    /// <summary>16 digits, as its own uploader mints. Ties this file's chunks together, so two
    /// concurrent uploads must never share one.</summary>
    internal static string NewUploadSid()
    {
        Span<char> digits = stackalloc char[16];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10));
        }

        return new string(digits);
    }

    private async Task<(string? Node, string? Error)> StartUploadAsync(AttemptContext ctx, string sessionId)
    {
        _ = sessionId;

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "start_upload",
            ["file_name"] = ctx.FileName,
            ["file_descr"] = string.Empty,
            ["file_public"] = "1",
            ["file_size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
        };

        HttpResponseSnapshot response;
        try
        {
            response = await PostFormAsync(ctx, Host + "/", form);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"DataNodes upload-node lookup failed: {ex.Message}");
        }

        return ParseNode(response);
    }

    /// <summary>Reads the node out of the start_upload reply. Internal for testing.</summary>
    internal static (string? Node, string? Error) ParseNode(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"DataNodes wouldn't name an upload node (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;
            return root.TryGetProperty("url", out JsonElement u) && u.GetString() is { Length: > 0 } url
                ? (url.TrimEnd('/'), null)
                : (null, $"DataNodes' node lookup carried no url: {Snippet(response.Body)}");
        }
        catch (JsonException)
        {
            return (null, $"DataNodes' node lookup wasn't JSON: {Snippet(response.Body)}");
        }
    }

    /// <summary>Sends the file as offset-tagged chunks. Null on success, else which chunk failed.</summary>
    private async Task<string?> SendChunksAsync(AttemptContext ctx, string node, string sid, string sessionId)
    {
        _ = sessionId;

        long fileSize = ctx.FileSize;
        int totalChunks = fileSize <= ChunkSizeBytes ? 1 : (int)((fileSize + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;
        string endpoint = node + "/put_chunk_mt.cgi";

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        long position = 0;

        for (int index = 0; index < totalChunks; index++)
        {
            long thisChunk = Math.Min(ChunkSizeBytes, fileSize - position);

            // X-Seek-To is what makes this the "mt" variant: the chunk says where it belongs, so the
            // server doesn't rely on arrival order.
            Dictionary<string, string> headers = BrowserHeaders();
            headers["X-Upload-SID"] = sid;
            headers["X-Seek-To"] = position.ToString(CultureInfo.InvariantCulture);

            HttpResponseSnapshot response;
            if (_chunkOverride is not null)
            {
                response = await _chunkOverride(endpoint, headers, thisChunk);
            }
            else
            {
                file.Position = position;
                response = await ctx.Handler.PutChunkAsync(
                    endpoint,
                    new ChunkSliceStream(file, thisChunk),
                    thisChunk,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    headers: headers,
                    speedBudget: ctx.SpeedBudget,
                    cancellationToken: ctx.Cancellation);
            }

            if (response.StatusCode is < 200 or >= 300)
            {
                return $"DataNodes rejected chunk {(index + 1).ToString(CultureInfo.InvariantCulture)}"
                    + $"/{totalChunks.ToString(CultureInfo.InvariantCulture)} "
                    + $"(HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}";
            }

            position += thisChunk;
        }

        return null;
    }

    private async Task<(string? Link, string? DeleteLink, string? Error)> ImportFileAsync(
        AttemptContext ctx, string node, string sid, string sessionId)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "import_file",
            ["sid"] = sid,
            ["fname"] = ctx.FileName,

            // Empty for a guest, the account's xfss when signed in. The one field that differs.
            ["sess_id"] = sessionId,
            ["file_public"] = "1",
            ["link_rcpt"] = string.Empty,
            ["link_pass"] = string.Empty,
            ["to_folder"] = "0",
        };

        HttpResponseSnapshot response;
        try
        {
            response = await PostFormAsync(ctx, node + "/api.cgi", form);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, $"DataNodes took the file but the upload couldn't be finalised: {ex.Message}");
        }

        return ParseImportResponse(response);
    }

    /// <summary>Reads the finalise reply — the share link and the killcode delete link. Internal for
    /// testing.</summary>
    internal static (string? Link, string? DeleteLink, string? Error) ParseImportResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            // Seen once in probing and not reproducible: this host can 500 here with every byte
            // already uploaded, so the message says the transfer isn't what failed.
            return (null, null, $"DataNodes took the file but refused to finalise it "
                + $"(HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;

            if (!root.TryGetProperty("links", out JsonElement links)
                || !links.TryGetProperty("download_link", out JsonElement dl)
                || dl.GetString() is not { Length: > 0 } link)
            {
                return (null, null, $"DataNodes finalised the upload but returned no link: {Snippet(response.Body)}");
            }

            string? delete = links.TryGetProperty("delete_link", out JsonElement del) ? NullIfWhiteSpace(del.GetString()) : null;
            return (link, delete, null);
        }
        catch (JsonException)
        {
            return (null, null, $"DataNodes' finalise reply wasn't JSON: {Snippet(response.Body)}");
        }
    }

    /// <summary>The account's <c>xfss</c>: the stored one, else a fresh sign-in.</summary>
    private async Task<(string? Session, string? Error)> ResolveSessionAsync(AttemptContext ctx)
    {
        if (NullIfWhiteSpace(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        return await SignInAsync(
            ctx.Credentials.Username,
            ctx.Credentials.Password,
            url => GetAsync(ctx, url),
            (url, form) => PostFormAsync(ctx, url, form));
    }

    /// <summary>
    /// Posts the site's plain login form — <c>op=login</c> with the page's anti-CSRF token, no
    /// captcha — and takes the <c>xfss</c> cookie the 302 sets.
    /// </summary>
    private static async Task<(string? Session, string? Error)> SignInAsync(
        string? username,
        string? password,
        Func<string, Task<HttpResponseSnapshot>> get,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> post)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "DataNodes needs the account's username and password.");
        }

        string token;
        try
        {
            HttpResponseSnapshot page = await get(LoginPageUrl);
            token = LoginTokenRegex.Match(page.Body) is { Success: true } m ? m.Groups[1].Value : string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "DataNodes login page fetch failed: " + ex.Message);
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["op"] = "login",
            ["token"] = token,
            ["rand"] = string.Empty,
            ["redirect"] = Host + "/",
            ["login"] = username,
            ["password"] = password,
        };

        HttpResponseSnapshot login;
        try
        {
            login = await post(Host + "/", form);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "DataNodes login request failed: " + ex.Message);
        }

        return ReadSessionCookie(login) is { } session
            ? (session, null)
            : (null, "DataNodes rejected the sign-in — check the username and password.");
    }

    /// <summary>Pulls <c>xfss</c> from the login reply. Its absence is how a bad password shows —
    /// the family re-renders the form rather than answering an error. Internal for testing.</summary>
    internal static string? ReadSessionCookie(HttpResponseSnapshot response)
    {
        foreach (string cookie in response.SetCookies)
        {
            if (cookie.StartsWith("xfss=", StringComparison.OrdinalIgnoreCase))
            {
                string value = cookie.Split(';', 2)[0]["xfss=".Length..];

                // A logout sets an empty xfss; treat that as no session rather than a usable one.
                if (!string.IsNullOrWhiteSpace(value) && value != "deleted")
                {
                    return value;
                }
            }
        }

        return null;
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? session, string? error) = await SignInAsync(
            username,
            password,
            url => GetAsync(handler, url, BrowserHeaders(), ct),
            (url, form) => _postFormOverride is not null ? _postFormOverride(url, form) : handler.PostFormAsync(url, form, BrowserHeaders(), ct));

        if (session is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error);
        }

        // The cookie already proves the password. The account page is asked for the plan and the
        // storage figures — and if it comes back showing a signed-OUT visitor, the session it just
        // issued is worthless, so say so now rather than at the first upload.
        HttpResponseSnapshot? page = await GetAccountPageAsync(handler, session, ct);
        (bool signedIn, AccountType type, long? used, long? quota) = ParseAccountPage(page);

        if (page is not null && !signedIn)
        {
            return new AccountCheckResult(false, AccountType.Free, "DataNodes accepted the sign-in but wouldn't open the account page.");
        }

        return new AccountCheckResult(
            true,
            type,
            "Signed in to DataNodes.",
            SessionCookie: session,
            SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),

            // The username as typed: it is the identifier the next sign-in posts, so nothing scraped
            // may replace it.
            DerivedUsername: username,
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>Re-checks the stored <c>xfss</c> without a password by asking for the account page —
    /// a dead session is bounced to the login form.</summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        HttpResponseSnapshot? page = await GetAccountPageAsync(handler, sessionCookie, ct);
        (bool signedIn, AccountType type, long? used, long? quota) = ParseAccountPage(page);

        if (!signedIn)
        {
            return new AccountCheckResult(false, AccountType.Free, "The saved DataNodes sign-in is no longer valid — sign in again.");
        }

        return new AccountCheckResult(
            true,
            type,
            "Signed in to DataNodes.",
            SessionCookie: sessionCookie,
            SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),
            StorageUsedBytes: used,
            StorageQuotaBytes: quota);
    }

    /// <summary>Fetches <c>/account</c> under the session. Null when the request itself failed, which
    /// is not the same answer as the page saying "logged out".</summary>
    private async Task<HttpResponseSnapshot?> GetAccountPageAsync(HttpHandler handler, string sessionCookie, CancellationToken ct)
    {
        Dictionary<string, string> headers = BrowserHeaders();
        headers["Cookie"] = "xfss=" + sessionCookie;

        try
        {
            return await GetAsync(handler, AccountPageUrl, headers, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the account page: whether it is the signed-in one at all, then the plan and the storage
    /// tile. Internal for testing.
    /// <para>
    /// The signed-in marker is <b><c>/logout</c></b>, not the family's <c>op=logout</c> — this fork's
    /// template links it plainly, exactly as DDownload's does, and matching on the stock form made a
    /// live, working session read as expired. A dead one never gets this far: <c>/account</c> answers
    /// it with a 302 to the login page and an empty body.
    /// </para>
    /// </summary>
    internal static (bool SignedIn, AccountType Type, long? Used, long? Quota) ParseAccountPage(HttpResponseSnapshot? page)
    {
        if (page is null || page.StatusCode is < 200 or >= 300 || !page.Body.Contains("/logout", StringComparison.OrdinalIgnoreCase))
        {
            return (false, AccountType.Free, null, null);
        }

        // Premium's wording is unverified — only a free account was available — so anything that
        // isn't recognisably premium is reported as free rather than guessed upward.
        AccountType type = PlanRegex.Match(page.Body) is { Success: true } plan
            && plan.Groups[1].Value.Equals("Premium", StringComparison.OrdinalIgnoreCase)
            ? AccountType.Premium
            : AccountType.Free;

        if (StorageRegex.Match(page.Body) is not { Success: true } storage)
        {
            return (true, type, null, null);
        }

        string unit = storage.Groups[3].Value;
        return (
            true,
            type,
            XFileSharingApiPipeline.ParseSizeToBytes(storage.Groups[1].Value, unit),
            XFileSharingApiPipeline.ParseSizeToBytes(storage.Groups[2].Value, unit));
    }

    private Task<HttpResponseSnapshot> PostFormAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string> form)
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : ctx.Handler.PostFormAsync(url, form, BrowserHeaders(), ctx.Cancellation);

    private Task<HttpResponseSnapshot> GetAsync(AttemptContext ctx, string url)
        => GetAsync(ctx.Handler, url, BrowserHeaders(), ctx.Cancellation);

    private Task<HttpResponseSnapshot> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _getOverride is not null
            ? _getOverride(url, headers)
            : handler.GetSnapshotAsync(url, headers, ct);

    /// <summary>No cookie here on purpose: this host sets none, and the whole chain works from a cold
    /// client. Only the account path adds one, and only to the pages that need it.</summary>
    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/upload",
    };

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
