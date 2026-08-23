// <copyright file="FileMiragePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FileMirage (filemirage.com) — <b>50 GiB</b>, chunked, anonymous or signed in. The service publishes
/// its API on the account dashboard (<c>/user/api</c>), and the flow is two calls:
/// <list type="number">
///   <item><b>Ask which node.</b> <c>GET /api/servers</c> →
///   <c>{"data":{"server":"https://storeN.filemirage.com","upload_id":"…"}}</c>. It answers keylessly
///   and <b>ignores any token entirely</b> — a bogus bearer gets the same reply as a real one.</item>
///   <item><b>Send the chunks.</b> Multipart POST per chunk to <c>&lt;server&gt;/upload.php</c> with
///   <c>file</c>, <c>filename</c>, <c>upload_id</c>, <c>chunk_number</c> (0-based) and
///   <c>total_chunks</c>. The last chunk answers <c>{"data":{"url":"…"}}</c>.</item>
/// </list>
/// <para>
/// <b>⚠ THE ACCOUNT IS ONE HEADER, AND GETTING IT WRONG FAILS SILENTLY.</b> A signed-in upload is
/// byte-identical to an anonymous one except for <c>Authorization: Bearer &lt;api_token&gt;</c>, and
/// the host's own documentation spells out the trap: <i>"if not set the file will be uploaded as
/// visitor"</i>. Confirmed live — a deliberately wrong token returned <b>200 with a working link</b>
/// and the file simply never appeared in the account. Nothing downstream can detect that, so this
/// pipeline <b>refuses to upload</b> when an account was chosen but no token is available, rather
/// than quietly handing the user an anonymous link. See <c>RunAsync</c>.
/// </para>
/// <para>
/// <b>The token is a durable per-account key, not a session artefact</b> — it is printed on
/// <c>/user/api</c> as "Your API Token" (<c>XXXX-XXXX-XXXX-XXXX</c>), and a fresh login hours later
/// returns the same value. It still cannot be accepted as a pasted credential, because nothing on the
/// service can tell a good token from a bad one: a typo would upload every file as a visitor forever.
/// So <see cref="CheckAccountAsync"/> takes the email and password, signs in through the site's plain
/// form (no captcha), and <b>derives</b> the token — which then lives in the API-key slot and needs no
/// cookie at upload time.
/// </para>
/// <para>
/// <b>The upload id comes from the node lookup</b>, as the API documents. Its own web uploader ignores
/// that and mints <c>Date.now().toString(36)</c> instead, which collides for two files started in the
/// same millisecond — and a collision means two files assembled into each other. Random bytes are the
/// fallback when a lookup carries no id.
/// </para>
/// <para>
/// Its page declares <c>upload_chunk_size: 99</c> (MB) and <c>maxFileSize = 53687091200</c>, both used
/// here as given, and both identical for guests and accounts — an account buys file management and a
/// longer inactivity window (20 days free), not a bigger file.
/// </para>
/// </summary>
public sealed class FileMiragePipeline : IFileHosterPipeline
{
    private const string Host = "https://filemirage.com";
    private const string ServersUrl = Host + "/api/servers";

    /// <summary>The page's own <c>maxFileSize</c> (50 GiB).</summary>
    private const long MaxFileSizeBytes = 53_687_091_200;

    /// <summary>The page's own <c>upload_chunk_size: 99</c>, in MB as its uploader multiplies it.</summary>
    private const int ChunkSizeBytes = 99 * 1024 * 1024;

    private const string LoginPageUrl = Host + "/login";
    /// <summary>Every page carries the signed-in account's token; an anonymous one carries an empty
    /// string, which is exactly the "upload as visitor" case.</summary>
    private static readonly Regex ApiTokenRegex = new(
        """const\s+api_token\s*=\s*"([^"]*)"\s*;""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Laravel's CSRF field on the login form.</summary>
    private static readonly Regex CsrfRegex = new(
        """name="_token"[^>]*\svalue="([^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>>? _chunkOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;

    public FileMiragePipeline()
    {
    }

    /// <summary>Test ctor — stubs every request this host makes: the node lookup and page GETs, the
    /// per-chunk POST, and the login form POST.</summary>
    internal FileMiragePipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>>? chunkOverride = null,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
    {
        _getOverride = getOverride;
        _chunkOverride = chunkOverride;
        _postFormOverride = postFormOverride;
    }

    public string Name => "FileMirage";

    /// <summary>Downloads are captcha-free: a live probe's server-rendered download page has
    /// zero captcha markup and offers direct-link embeds (2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>20 days of INACTIVITY on a free account - what the free plan actually limits, rather
    /// than file size. Only that tier is documented: anonymous uploads and premium accounts report
    /// unknown instead of borrowing the figure.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => !credentials.IsAnonymous && credentials.AccountType != AccountType.Premium
            ? FileRetention.DaysAfterLastDownload(20)
            : FileRetention.Unspecified;

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds FileMirage's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0: the account, if one was chosen ===
        // The host uploads as a visitor when the bearer is missing and says so in its own docs, so a
        // signed-in attempt with no token would succeed, return a link, and silently leave the file
        // out of the account. Refusing is the only honest outcome: nothing later can detect it.
        string? token = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            token = NullIfWhiteSpace(ctx.Credentials.ApiKey);
            if (token is null)
            {
                yield return new AttemptFailed(
                    "FileMirage has no upload token for this account, and uploading without one would "
                    + "put the file in as a visitor instead. Re-check the account in Account Manager.",
                    null);
                yield break;
            }
        }

        // === Step 1: which node ===
        (string? node, string? serverUploadId, string? lookupError) = await ResolveNodeAsync(ctx);
        if (node is null)
        {
            yield return new AttemptFailed(lookupError!, null);
            yield break;
        }

        // === Step 2: the chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<(string? Url, string? Error)> uploadTask = SendChunksAsync(ctx, node, serverUploadId, token);
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

        (string? url, string? error) = await uploadTask;
        if (url is null)
        {
            yield return new AttemptFailed(error ?? "FileMirage upload failed", null);
            yield break;
        }

        yield return new TransferCompleted(url);
    }

    private async Task<(string? Node, string? UploadId, string? Error)> ResolveNodeAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot response;
        try
        {
            response = _getOverride is not null
                ? await _getOverride(ServersUrl)
                : await ctx.Handler.GetSnapshotAsync(ServersUrl, BrowserHeaders(null), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, $"FileMirage upload-node lookup failed: {ex.Message}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"FileMirage wouldn't name an upload node (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        (string? server, string? uploadId) = ReadNode(response.Body);
        return server is null
            ? (null, null, $"FileMirage's node lookup carried no server: {Snippet(response.Body)}")
            : (server.TrimEnd('/'), uploadId, null);
    }

    /// <summary>Reads <c>data.server</c> and <c>data.upload_id</c> out of the lookup. Internal for
    /// testing.</summary>
    internal static (string? Server, string? UploadId) ReadNode(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            if (!root.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("server", out JsonElement server)
                || server.ValueKind != JsonValueKind.String
                || server.GetString() is not { Length: > 0 } url)
            {
                return (null, null);
            }

            string? id = data.TryGetProperty("upload_id", out JsonElement uploadId)
                         && uploadId.ValueKind == JsonValueKind.String
                ? NullIfWhiteSpace(uploadId.GetString())
                : null;

            return (url, id);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task<(string? Url, string? Error)> SendChunksAsync(AttemptContext ctx, string node, string? serverUploadId, string? token)
    {
        string endpoint = $"{node}/upload.php";

        // The API documents the id as the one the lookup just handed back, and a server-minted id
        // cannot collide. Its own web uploader ignores that and keys the id on the clock instead,
        // which collides for two files started in the same millisecond — and a collision means two
        // files assembled into one. Random bytes are the fallback, never the timestamp.
        string uploadId = serverUploadId ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        long fileSize = ctx.FileSize;
        int totalChunks = fileSize <= ChunkSizeBytes ? 1 : (int)((fileSize + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long position = 0;

        for (int index = 0; index < totalChunks; index++)
        {
            long thisChunk = Math.Min(ChunkSizeBytes, fileSize - position);
            Dictionary<string, string> fields = new(StringComparer.Ordinal)
            {
                ["filename"] = ctx.FileName,
                ["upload_id"] = uploadId,
                ["chunk_number"] = index.ToString(CultureInfo.InvariantCulture),
                ["total_chunks"] = totalChunks.ToString(CultureInfo.InvariantCulture),
            };

            HttpResponseSnapshot response;
            if (_chunkOverride is not null)
            {
                response = await _chunkOverride(endpoint, fields, BrowserHeaders(token), thisChunk);
            }
            else
            {
                file.Position = position;
                response = await ctx.Handler.PostChunkMultipartAsync(
                    endpoint,
                    new ChunkSliceStream(file, thisChunk),
                    thisChunk,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    fileFieldName: "file",
                    filePartName: ctx.FileName,
                    ctx.SpeedBudget,
                    extraFields: fields,
                    headers: BrowserHeaders(token),
                    cancellationToken: ctx.Cancellation);
            }

            (string? url, string? error) = ParseChunkResponse(response, index, totalChunks);
            if (error is not null)
            {
                return (null, error);
            }

            if (url is not null)
            {
                return (url, null);
            }

            position += thisChunk;
        }

        // Every chunk was accepted and none carried a link — the host changed its reply shape, and a
        // "successful" upload with no link is not one.
        return (null, "FileMirage accepted every chunk but returned no link.");
    }

    /// <summary>
    /// Reads one chunk reply: intermediate chunks answer without a URL, the last one carries
    /// <c>data.url</c>. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseChunkResponse(HttpResponseSnapshot response, int index, int total)
    {
        string where = $"chunk {(index + 1).ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}";

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"FileMirage rejected {where} (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"FileMirage's reply to {where} wasn't JSON: {Snippet(response.Body)}");
        }

        // The envelope carries its own flag and a false one can ride inside a 200. There are TWO
        // spellings: the success envelope says "success", but the API's documented failure envelope
        // says "result" — reading only one of them turns a stated refusal into a silent success.
        if ((root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
            || (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.False))
        {
            string? message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
            return (null, string.IsNullOrWhiteSpace(message)
                ? $"FileMirage refused {where}: {Snippet(response.Body)}"
                : $"FileMirage refused {where}: {message}");
        }

        string? url = root.TryGetProperty("data", out JsonElement data)
                      && data.TryGetProperty("url", out JsonElement u)
                      && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

        return (string.IsNullOrWhiteSpace(url) ? null : url, null);
    }

    /// <summary>Request headers for the node lookup and the chunks. <paramref name="token"/> is the
    /// account's API token, or null for a guest — its own client sends an <b>empty</b> authorization
    /// in that case rather than omitting the header, which is also what the host documents as
    /// "uploaded as visitor".</summary>
    private static Dictionary<string, string> BrowserHeaders(string? token) => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Accept"] = "application/json, text/plain, */*",
        ["authorization"] = token is null ? string.Empty : "Bearer " + token,
    };

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

    /// <summary>
    /// Signs in through the site's own login form and <b>derives</b> the account's upload token,
    /// which is then stored as the API key and is all an upload needs.
    /// <para>
    /// The token is never asked for directly even though it is durable and printed on
    /// <c>/user/api</c>: nothing on the service can distinguish a good token from a bad one — the
    /// node lookup ignores it and an upload with a wrong one returns 200 and a working link — so a
    /// mistyped key would put every file in as a visitor with no way to notice. A wrong <i>password</i>,
    /// by contrast, fails here and now.
    /// </para>
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new AccountCheckResult(false, AccountType.Free, "FileMirage needs the account's email address and password.");
        }

        Dictionary<string, string> jar = new(StringComparer.Ordinal);

        // The login form is plain and un-captcha'd, but Laravel still requires its CSRF pair: the
        // _token field must match the session cookie the login page just issued.
        HttpResponseSnapshot loginPage;
        try
        {
            loginPage = await GetAsync(handler, LoginPageUrl, PageHeaders(jar), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "FileMirage login page fetch failed: " + ex.Message);
        }

        MergeCookies(jar, loginPage.SetCookies);
        if (CsrfRegex.Match(loginPage.Body) is not { Success: true } csrf)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                $"FileMirage's login page carried no sign-in token (HTTP {loginPage.StatusCode}).");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["email"] = username,
            ["password"] = password,
            ["remember-me"] = "1",
            ["_token"] = csrf.Groups[1].Value,
        };

        HttpResponseSnapshot login;
        try
        {
            login = _postFormOverride is not null
                ? await _postFormOverride(LoginPageUrl, form, PageHeaders(jar, LoginPageUrl))
                : await handler.PostFormAsync(LoginPageUrl, form, PageHeaders(jar, LoginPageUrl), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "FileMirage login request failed: " + ex.Message);
        }

        MergeCookies(jar, login.SetCookies);

        // BOTH outcomes are a 302, so the status says nothing: a rejected sign-in redirects straight
        // back to /login (Laravel's "invalid credentials" round-trip) while a good one goes to the
        // site root. The Location is the only thing that separates them, so it is what's read.
        if (login.StatusCode is not (>= 300 and < 400) || LooksLikeLoginPage(login.LocationHeader))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                $"FileMirage rejected the sign-in — check the email and password (HTTP {login.StatusCode}).");
        }

        // The token is on every signed-in page. An EMPTY one is the real failure mode here: it means
        // the session didn't take, and it is precisely the value that uploads as a visitor.
        HttpResponseSnapshot home;
        try
        {
            home = await GetAsync(handler, Host + "/", PageHeaders(jar), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "FileMirage account page fetch failed: " + ex.Message);
        }

        string? token = ReadApiToken(home.Body);
        if (token is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "FileMirage signed in but issued no upload token, so uploads would go in as a visitor.");
        }

        // NO DerivedUsername is returned, deliberately. The verifier's value is copied straight onto
        // the DTO's Username, and for a classic username/password hoster that IS the login identifier —
        // handing back the display name from /user/settings ("csuprobe") would overwrite the email and
        // every later re-check would sign in as a user that doesn't exist. The email the user typed is
        // already the right thing to show.
        // Storage is "unlimited" on every plan the service sells, so there is no quota to report —
        // what the free plan actually limits is inactivity (files go after 20 days untouched).
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in. Free accounts keep a file for 20 days after its last activity.",
            ApiKey: token);
    }

    private Task<HttpResponseSnapshot> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url) : handler.GetSnapshotAsync(url, headers, ct);

    /// <summary>True when a login POST's redirect points back at the sign-in page, which is how this
    /// host says "wrong credentials" — the status code is a 302 either way. Internal for testing.</summary>
    internal static bool LooksLikeLoginPage(string? location)
        => location is not null
           && (location.EndsWith("/login", StringComparison.OrdinalIgnoreCase)
               || location.Contains("/login?", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pulls <c>const api_token = "…"</c> off a page. Null when absent <b>or empty</b> — an
    /// empty token is what an anonymous page carries, and using it uploads as a visitor. Internal for
    /// testing.</summary>
    internal static string? ReadApiToken(string body)
        => ApiTokenRegex.Match(body) is { Success: true } m ? NullIfWhiteSpace(m.Groups[1].Value) : null;

    /// <summary>Headers for the ordinary web pages the sign-in walks (not the API).</summary>
    private static Dictionary<string, string> PageHeaders(Dictionary<string, string> jar, string? referer = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        };

        if (jar.Count > 0)
        {
            headers["Cookie"] = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        if (referer is not null)
        {
            headers["Origin"] = Host;
            headers["Referer"] = referer;
        }

        return headers;
    }

    /// <summary>Folds <c>Set-Cookie</c> into the jar. The handler runs with <c>UseCookies=false</c>, so
    /// the sign-in walk carries its own session by hand.</summary>
    private static void MergeCookies(Dictionary<string, string> jar, IReadOnlyList<string> setCookies)
    {
        foreach (string cookie in setCookies)
        {
            string pair = cookie.Split(';', 2)[0];
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                jar[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
