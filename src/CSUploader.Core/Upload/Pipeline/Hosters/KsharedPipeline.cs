// <copyright file="KsharedPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// kshared (www.kshared.com) — <b>ACCOUNT-ONLY</b>, a JSON API behind a single-page "drive":
/// <list type="number">
///   <item><b>Sign in.</b> <c>POST /v1/account/signin</c> <c>{email, passw, robo:"__"}</c> →
///   an envelope carrying <b>three</b> credentials (see below). No captcha.</item>
///   <item><b>Node.</b> <c>POST /v1/drive/get_server_for_upload</c> with the file's size →
///   <c>{kind:true, ID, uri, token}</c>.</item>
///   <item><b>Upload.</b> One multipart POST to <c>uri</c> carrying <c>ui</c>, <c>ut</c>, <c>ud</c>,
///   a client-minted <c>ID</c>, <c>dir</c>, <c>server</c>, <c>token</c> and the file →
///   <c>{kind:"fileSaved", file:{ID, …}}</c>. The link is <c>/file/&lt;file.ID&gt;</c>.</item>
/// </list>
/// <para>
/// <b>⚠ THREE tokens, and which goes where is not guessable.</b> The sign-in reply returns
/// <c>accesstoken</c> (a JWT), <c>ut</c> (a base64 blob) and <c>hash</c> (a SECOND, longer JWT), and
/// every one of them is used somewhere different: <c>hash</c> is the <c>Authorization: Bearer</c>,
/// <c>accesstoken</c> is the body/multipart field <c>ud</c>, and <c>ut</c> is the field <c>ut</c>.
/// Getting this wrong does not produce an auth error — see the next paragraph.
/// </para>
/// <para>
/// <b>⚠⚠ Its errors point at the wrong thing, twice.</b> Sending the <c>accesstoken</c> as the Bearer
/// makes the node call answer <c>{"error":true,"reason":"sessionExpired"}</c>, which reads as "your
/// sign-in has lapsed" when the sign-in is seconds old. And sending the wrong value as the upload's
/// <c>ut</c> makes the NODE answer <c>{"error":true,"reason":"disk"}</c> — a storage message for what
/// is actually a bad token, and one that cost real time here before a corrected request uploaded the
/// same 2 MB file without complaint. Neither message can be taken at face value.
/// </para>
/// <para>
/// <b>It also needs the PHP session.</b> A <c>GET /</c> first, and the resulting <c>PHPSESSID</c> on
/// every API call; without it the calls answer <c>sessionExpired</c> as well.
/// </para>
/// <para>
/// <b>This is the third host seen on Emload's engine</b> (<see cref="EmloadPipeline"/>), after
/// jumploads — but the dialects have diverged far enough that a shared base would abstract almost
/// nothing: Emload authenticates with four JS-set cookies and a throwaway nonce Bearer, kshared with a
/// PHP session and a real JWT Bearer, and even the route names differ
/// (<c>get_available_server</c> vs <c>get_server_for_upload</c>). What they genuinely share is the
/// upload multipart's field set and the <c>{kind:"fileSaved"}</c> reply. Extract a base when a fourth
/// appears, or when two of them agree on the auth model.
/// </para>
/// <para>
/// <b>No per-file cap is claimed.</b> The node call is told the size and answered a hundred gigabytes
/// without complaint, and nothing on the site publishes a figure — so the host's own answer is the
/// gate rather than a guess. Verified live: 1 KB and 2 MB both stored, and their links serve pages
/// naming the file.
/// </para>
/// </summary>
public sealed class KsharedPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://www.kshared.com";
    private const string SignInUrl = Host + "/v1/account/signin";
    private const string NodeUrl = Host + "/v1/drive/get_server_for_upload";

    /// <summary>The <c>accesstoken</c> JWT's own lifetime: <c>exp - iat</c> is 604800 seconds.</summary>
    private const int SessionLifetimeDays = 7;

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _uploadOverride;

    public KsharedPipeline()
    {
    }

    /// <summary>Test ctor — stubs the page GET, the JSON API calls and the file upload.</summary>
    internal KsharedPipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? uploadOverride = null)
    {
        _getOverride = getOverride;
        _postJsonOverride = postJsonOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "kshared";

    /// <summary>Free downloads are captcha-gated: its premium page lists "No captcha codes"
    /// among every tier's paid benefits (kshared.com/premium, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>Nothing published, and its own pre-flight accepted 100 GB — so the host's answer is
    /// the gate rather than a number invented here.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Every call needs the signed-in tokens; there is no guest route.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed(
                "kshared has no anonymous upload — every one of its API calls needs a signed-in session. "
                + "Add a kshared account in Account Manager.",
                null);
            yield break;
        }

        (KsharedSession? session, string? authError) = await ResolveSessionAsync(ctx);
        if (session is null)
        {
            yield return new AttemptFailed(authError!, null);
            yield break;
        }

        (KsharedNode? node, string? nodeError) = await GetNodeAsync(ctx, session);
        if (node is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, session, node);
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

        HttpResponseSnapshot? response = null;
        Exception? transferFault = null;
        try
        {
            response = await uploadTask;
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            transferFault = ex;
        }

        if (transferFault is not null)
        {
            yield return new AttemptFailed($"kshared upload failed: {transferFault.Message}", transferFault);
            yield break;
        }

        (string? link, string? uploadError) = ParseUploadResponse(response!);
        if (link is null)
        {
            yield return new AttemptFailed(uploadError!, null);
            yield break;
        }

        yield return new TransferCompleted(link);
    }

    /// <summary>The stored session, else a fresh sign-in with the saved email and password.</summary>
    private async Task<(KsharedSession? Session, string? Error)> ResolveSessionAsync(AttemptContext ctx)
    {
        if (KsharedSession.TryParse(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        return await SignInAsync(
            ctx.Credentials.Username,
            ctx.Credentials.Password,
            url => GetAsync(ctx.Handler, url, BrowserHeaders(), ctx.Cancellation),
            (url, json, headers) => PostJsonAsync(ctx.Handler, url, json, headers, ctx.Cancellation));
    }

    private async Task<(KsharedNode? Node, string? Error)> GetNodeAsync(AttemptContext ctx, KsharedSession session)
    {
        string json = JsonSerializer.Serialize(new
        {
            remote = 0,
            remoteUri = (string?)null,
            service = (string?)null,
            size = ctx.FileSize,

            // ud is the accesstoken and ut is the blob — NOT interchangeable, and not the Bearer.
            ud = session.AccessToken,
            ut = session.Ut,
            ___uctmp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        HttpResponseSnapshot response;
        try
        {
            response = await PostJsonAsync(ctx.Handler, NodeUrl, json, ApiHeaders(session), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"kshared upload-node lookup failed: {ex.Message}");
        }

        return ParseNode(response);
    }

    /// <summary>Reads the node reply. Internal for testing.</summary>
    internal static (KsharedNode? Node, string? Error) ParseNode(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"kshared wouldn't name an upload node (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"kshared's node lookup wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is { } reason)
        {
            return (null, DescribeReason(reason, "the upload-node lookup"));
        }

        if (!root.TryGetProperty("uri", out JsonElement uriElement) || uriElement.GetString() is not { Length: > 0 } uri)
        {
            return (null, $"kshared's node lookup carried no upload server: {Snippet(response.Body)}");
        }

        string id = root.TryGetProperty("ID", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        string token = root.TryGetProperty("token", out JsonElement tokenElement) ? tokenElement.GetString() ?? string.Empty : string.Empty;

        return (new KsharedNode(uri, id, token), null);
    }

    private Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, KsharedSession session, KsharedNode node)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["ui"] = session.Uid,

            // ⚠ The ACCOUNT's ut, not the node's token — the node has a separate field for that, and
            // swapping them earns a "disk" error rather than an auth one.
            ["ut"] = session.Ut,
            ["ud"] = session.AccessToken,

            // Ties this file to the reservation just made, so two uploads must never share one.
            ["ID"] = Guid.NewGuid().ToString("N"),
            ["dir"] = "root",
            ["server"] = node.Id,
            ["token"] = node.Token,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        return _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, node.Uri, fields)
            : ctx.Handler.UploadMultipartAsync(
                ctx.FilePath, node.Uri, "file", fields, headers, ctx.SpeedLimitProvider, ctx.Cancellation);
    }

    /// <summary>Reads the upload reply into the share link its own site builds — <c>/file/{ID}</c>.
    /// Internal for testing.</summary>
    internal static (string? Link, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"kshared rejected the upload (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        if (!TryParseTolerantly(response.Body, out JsonElement root))
        {
            return (null, $"kshared's upload reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is { } reason)
        {
            return (null, DescribeReason(reason, "the upload"));
        }

        if (!root.TryGetProperty("file", out JsonElement file)
            || !file.TryGetProperty("ID", out JsonElement id)
            || id.GetString() is not { Length: > 0 } fileId)
        {
            return (null, $"kshared took the file but returned no link: {Snippet(response.Body)}");
        }

        return ($"{Host}/file/{fileId}", null);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (KsharedSession? session, string? error) = await SignInAsync(
            username,
            password,
            url => GetAsync(handler, url, BrowserHeaders(), ct),
            (url, json, headers) => PostJsonAsync(handler, url, json, headers, ct));

        return session is null
            ? new AccountCheckResult(false, AccountType.Free, error)
            : new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in to kshared.",
                SessionCookie: session.ToStoredValue(),
                SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),

                // The email as typed: it is the identifier the next sign-in posts.
                DerivedUsername: NullIfWhiteSpace(session.Email) ?? username);
    }

    /// <summary>
    /// Fetches the site once for its PHP session, then posts the sign-in. Both halves matter: without
    /// the <c>PHPSESSID</c> the API answers <c>sessionExpired</c> to a perfectly good token.
    /// </summary>
    private static async Task<(KsharedSession? Session, string? Error)> SignInAsync(
        string? username,
        string? password,
        Func<string, Task<HttpResponseSnapshot>> get,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postJson)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "kshared needs the account's email address and password.");
        }

        string? php;
        try
        {
            HttpResponseSnapshot home = await get(Host + "/");
            php = ReadPhpSession(home);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "kshared home-page fetch failed: " + ex.Message);
        }

        Dictionary<string, string> headers = BrowserHeaders();
        if (php is not null)
        {
            headers["Cookie"] = php;
        }

        string json = JsonSerializer.Serialize(new
        {
            email = username,
            passw = password,
            robo = "__",
            ud = (string?)null,
            ut = (string?)null,
            ___uctmp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        HttpResponseSnapshot response;
        try
        {
            response = await postJson(SignInUrl, json, headers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "kshared sign-in failed: " + ex.Message);
        }

        return ParseSignIn(response, ReadPhpSession(response) ?? php);
    }

    /// <summary>
    /// Reads the sign-in reply into the FOUR values later calls need: the account id, and the three
    /// tokens that each go somewhere different. Internal for testing.
    /// </summary>
    internal static (KsharedSession? Session, string? Error) ParseSignIn(HttpResponseSnapshot response, string? phpSession)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"kshared's sign-in reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is { } reason)
        {
            string? message = root.TryGetProperty("message", out JsonElement m) ? NullIfWhiteSpace(m.GetString()) : null;
            return (null, message ?? "kshared rejected the sign-in — check the email address and password.");
        }

        string? uid = ReadString(root, "ID");
        string? accessToken = ReadString(root, "accesstoken");
        string? ut = ReadString(root, "ut");
        string? hash = ReadString(root, "hash");

        return uid is null || accessToken is null || ut is null || hash is null
            ? (null, $"kshared signed in but issued an incomplete session: {Snippet(response.Body)}")
            : (new KsharedSession(uid, accessToken, ut, hash, phpSession, ReadString(root, "email")), null);
    }

    /// <summary>Re-checks a stored session by asking for an upload node — the lightest authenticated
    /// call this API has, and the one whose refusal actually matters for uploading.</summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        if (KsharedSession.TryParse(sessionCookie) is not { } session)
        {
            return Expired();
        }

        try
        {
            string json = JsonSerializer.Serialize(new
            {
                remote = 0,
                remoteUri = (string?)null,
                service = (string?)null,
                size = 1,
                ud = session.AccessToken,
                ut = session.Ut,
                ___uctmp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            HttpResponseSnapshot response = await PostJsonAsync(handler, NodeUrl, json, ApiHeaders(session), ct);

            if (ParseNode(response).Node is not null)
            {
                return new AccountCheckResult(
                    true,
                    AccountType.Free,
                    "Signed in to kshared.",
                    SessionCookie: sessionCookie,
                    SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Falls through to the same "sign in again" answer a rejected session gets.
        }

        return Expired();

        static AccountCheckResult Expired()
            => new(false, AccountType.Free, "The saved kshared sign-in is no longer valid — sign in again.");
    }

    /// <summary>
    /// Turns this API's error reason into something a user can act on. ⚠ Two of them are actively
    /// misleading and are re-worded rather than repeated: <c>sessionExpired</c> is what a wrong Bearer
    /// earns, and <c>disk</c> is what a wrong <c>ut</c> earns.
    /// </summary>
    private static string DescribeReason(string reason, string what) => reason switch
    {
        "sessionExpired" => "The saved kshared sign-in is no longer valid — re-check the account in Account Manager.",
        "disk" => "kshared refused the upload for lack of space. (Its node also answers this when the "
            + "session tokens are wrong, so if the account has room, re-check it in Account Manager.)",
        _ => $"kshared refused {what}: {reason}",
    };

    /// <summary>
    /// Parses the reply, tolerating junk printed before the JSON.
    /// <para>
    /// Observed live: its upload node emits <c>Notice: Undefined index: HTTP_USER_AGENT in
    /// …/core.php</c> ahead of a perfectly good <c>{"kind":"fileSaved"}</c> when a request arrives
    /// without a User-Agent. This app always sends one, so that particular notice shouldn't happen —
    /// but a backend that can print a warning into its response body once can do it again, and the
    /// cost of being strict here is the worst kind of wrong answer: a SUCCESSFUL upload reported as a
    /// failure, which earns the user a duplicate file when they retry.
    /// </para>
    /// <para>Deliberately narrow: it only retries from the first brace, and only after a straight
    /// parse has already failed.</para>
    /// </summary>
    internal static bool TryParseTolerantly(string body, out JsonElement root)
    {
        try
        {
            root = JsonDocument.Parse(body).RootElement;
            return true;
        }
        catch (JsonException)
        {
            int brace = body.IndexOf('{', StringComparison.Ordinal);
            if (brace > 0)
            {
                try
                {
                    root = JsonDocument.Parse(body[brace..]).RootElement;
                    return true;
                }
                catch (JsonException)
                {
                    // Falls through: not a prefix problem.
                }
            }
        }

        root = default;
        return false;
    }

    /// <summary>The API's error reason, or null when the reply is a success. Failure is flagged by
    /// <c>error:true</c> and never by the status code.</summary>
    private static string? ReadErrorReason(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind is not JsonValueKind.True)
        {
            return null;
        }

        return root.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() ?? "unknown" : "unknown";
    }

    private static string? ReadPhpSession(HttpResponseSnapshot response)
        => response.SetCookies.FirstOrDefault(c => c.StartsWith("PHPSESSID=", StringComparison.OrdinalIgnoreCase))?.Split(';')[0];

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.GetString() is { Length: > 0 } value ? value : null;

    private Task<HttpResponseSnapshot> GetAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url, headers) : handler.GetSnapshotAsync(url, headers, ct);

    private Task<HttpResponseSnapshot> PostJsonAsync(HttpHandler handler, string url, string json, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _postJsonOverride is not null ? _postJsonOverride(url, json, headers) : handler.PostJsonAsync(url, json, headers, ct);

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Accept"] = "application/json, text/plain, */*",
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
    };

    /// <summary>Headers for an authenticated API call: the <c>hash</c> JWT as the Bearer — NOT the
    /// accesstoken — plus the PHP session.</summary>
    private static Dictionary<string, string> ApiHeaders(KsharedSession session)
    {
        Dictionary<string, string> headers = BrowserHeaders();
        headers["Authorization"] = "Bearer " + session.Hash;

        if (session.PhpSession is not null)
        {
            headers["Cookie"] = session.PhpSession;
        }

        return headers;
    }

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

    /// <summary>The upload server a node lookup named.</summary>
    internal sealed record KsharedNode(string Uri, string Id, string Token);

    /// <summary>
    /// A signed-in kshared session: the account id and the three tokens, each of which goes somewhere
    /// different, plus the PHP session every call needs. Stored as one JSON blob in the account's
    /// session-cookie slot because they are useless apart.
    /// </summary>
    internal sealed record KsharedSession(string Uid, string AccessToken, string Ut, string Hash, string? PhpSession, string? Email)
    {
        public string ToStoredValue() => JsonSerializer.Serialize(new
        {
            uid = Uid,
            accesstoken = AccessToken,
            ut = Ut,
            hash = Hash,
            php = PhpSession,
            email = Email,
        });

        /// <summary>Reads a stored session back, or null when any required part is missing — a partial
        /// one produces "sessionExpired", which would send the user hunting for the wrong problem.</summary>
        public static KsharedSession? TryParse(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
            {
                return null;
            }

            try
            {
                JsonElement root = JsonDocument.Parse(stored).RootElement;
                string? uid = ReadString(root, "uid");
                string? accessToken = ReadString(root, "accesstoken");
                string? ut = ReadString(root, "ut");
                string? hash = ReadString(root, "hash");

                return uid is null || accessToken is null || ut is null || hash is null
                    ? null
                    : new KsharedSession(uid, accessToken, ut, hash, ReadString(root, "php"), ReadString(root, "email"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
