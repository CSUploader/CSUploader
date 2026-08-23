// <copyright file="EmloadPipeline.cs" company="CSUploader">
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
/// Emload (emload.com) — <b>ACCOUNT-ONLY</b>, a JSON API behind a single-page "drive" app:
/// <list type="number">
///   <item><b>Sign in.</b> <c>POST /v2/app/user/signin</c> with <c>{em, passw, robo:"__"}</c> →
///   <c>{kind:"userSigned", uid, ut, ud, si}</c>. <c>ut</c> is a JWT good for seven days; no captcha.</item>
///   <item><b>Node.</b> <c>POST /v2/app/drive/get_available_server</c> with the file's <b>declared
///   size</b> → <c>{kind:true, server:{ID, uri, token}}</c>.</item>
///   <item><b>Upload.</b> One multipart POST to <c>server.uri</c> carrying <c>ui</c>, <c>ut</c>,
///   <c>ud</c>, the client-minted <c>ID</c>, <c>dir</c>, <c>server</c>, <c>token</c> and the file →
///   <c>{kind:"fileSaved", file:{token, …}, disk:&lt;bytes used&gt;}</c>.</item>
/// </list>
/// <para>
/// <b>⚠ Four cookies, not one.</b> The API is authenticated by <c>__uid</c>, <c>__ut</c>, <c>__ud</c>
/// AND <c>__si</c> — the four values the sign-in reply hands back — and the SPA sets them from its own
/// JavaScript, so <b>no <c>Set-Cookie</c> ever appears on the wire</b>. Send only the obvious two and
/// every call answers <c>{"error":true,"reason":"oauth"}</c>, which reads like a broken sign-in rather
/// than a missing header. All four are stored together as one cookie header.
/// </para>
/// <para>
/// <b>The node call is a real pre-flight, so this host has no guessed cap.</b> It is told the size
/// before a byte moves and answers <c>reason:"disk"</c> when the account hasn't the space — measured
/// against a free account: 20 GiB was accepted and 50 GiB refused, and the figure is remaining
/// storage rather than a per-file limit, so <see cref="MaxFileSize"/> is null and the host's own
/// answer is what stops an upload that couldn't have finished.
/// </para>
/// <para>
/// <b>Anonymous upload does not exist here</b> — the node call without a session token answers
/// <c>reason:"oauth"</c>.
/// </para>
/// <para>
/// The share link is the one the site's own script builds: <c>{base}file/{file.token}</c> with
/// <c>base = https://www.emload.com/v2/</c>. Note it is the file's <b>token</b>, not its <c>ID</c>,
/// and that the apex form without <c>/v2/</c> only redirects.
/// </para>
/// <para>
/// ⚠ The candidate list had this host as "Cloudflare 403 to all fetches — may be managed (Blocked)".
/// It is not: every path answers this client normally, sign-in included.
/// </para>
/// </summary>
public sealed class EmloadPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://www.emload.com";
    private const string ApiBase = Host + "/v2/app/";
    private const string SignInUrl = ApiBase + "user/signin";
    private const string NodeUrl = ApiBase + "drive/get_available_server";
    private const string TreeUrl = ApiBase + "drive/get_tree";

    /// <summary>The <c>ut</c> JWT's own lifetime: <c>exp - iat</c> is 604800 seconds.</summary>
    private const int SessionLifetimeDays = 7;

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _uploadOverride;

    public EmloadPipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON API calls and the file upload.</summary>
    internal EmloadPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? uploadOverride = null)
    {
        _postJsonOverride = postJsonOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "Emload";

    /// <summary>Free downloads are captcha-gated: the live free widget demands "Verify Captcha
    /// to Download" (reCAPTCHA v2) and its own core.js sells no-captcha as premium
    /// (2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No per-file cap to state. What limits an upload is the account's remaining storage, and
    /// the node call is told the size and refuses before any bytes move — a real answer beats a guess.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Measured: the node call without a session answers <c>reason:"oauth"</c>.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed(
                "Emload has no anonymous upload — its upload-node call refuses a caller with no account. "
                + "Add an Emload account in Account Manager.",
                null);
            yield break;
        }

        (EmloadSession? session, string? authError) = await ResolveSessionAsync(ctx);
        if (session is null)
        {
            yield return new AttemptFailed(authError!, null);
            yield break;
        }

        // The client mints the file's id, and it ties the node reservation to the upload that follows,
        // so two files must never share one.
        string fileId = Guid.NewGuid().ToString("N");

        (EmloadNode? node, string? nodeError) = await GetNodeAsync(ctx, session, fileId);
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

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, session, node, fileId);
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
            yield return new AttemptFailed($"Emload upload failed: {transferFault.Message}", transferFault);
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
    private async Task<(EmloadSession? Session, string? Error)> ResolveSessionAsync(AttemptContext ctx)
    {
        if (EmloadSession.TryParse(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        (EmloadSession? session, string? error) = await SignInAsync(
            ctx.Credentials.Username,
            ctx.Credentials.Password,
            (url, json) => PostJsonAsync(ctx.Handler, url, json, ApiHeaders(null), ctx.Cancellation));

        return session is null
            ? (null, error ?? "Emload has no usable sign-in for this account. Re-check it in Account Manager.")
            : (session, null);
    }

    private async Task<(EmloadNode? Node, string? Error)> GetNodeAsync(AttemptContext ctx, EmloadSession session, string fileId)
    {
        string json = JsonSerializer.Serialize(new
        {
            at = (string?)null,
            ut = session.Ut,
            file = new
            {
                ID = fileId,
                s3 = false,
                dir = "root",
                file = new { },
                remote = false,
                name = ctx.FileName,
                size = ctx.FileSize,
                type = "application/octet-stream",
                progress = 0,
                speed = 0,
                eta = 0,
                bytes = 0,
                status = 1,
            },
            remote = false,
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
            return (null, $"Emload upload-node lookup failed: {ex.Message}");
        }

        return ParseNode(response, ctx.FileSize);
    }

    /// <summary>
    /// Reads the node reply. The two error reasons worth telling apart are the two the user can act
    /// on: <c>disk</c> means the account is out of space for a file this size — caught here, before a
    /// byte moves — and <c>oauth</c> means the stored session is finished. Internal for testing.
    /// </summary>
    internal static (EmloadNode? Node, string? Error) ParseNode(HttpResponseSnapshot response, long fileSize)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Emload wouldn't name an upload node (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"Emload's node lookup wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is { } reason)
        {
            return (null, reason switch
            {
                "disk" => $"Emload hasn't the storage for this file "
                    + $"({ByteUnit.FromBytes(fileSize, ByteBase.Decimal).ToFriendlyString()}) — free space up in the "
                    + "account's drive, or upgrade the plan.",
                "oauth" => "The saved Emload sign-in is no longer valid — re-check the account in Account Manager.",
                _ => $"Emload refused the upload-node lookup: {reason}",
            });
        }

        if (!root.TryGetProperty("server", out JsonElement server)
            || server.TryGetProperty("uri", out JsonElement uriElement) is false
            || uriElement.GetString() is not { Length: > 0 } uri)
        {
            return (null, $"Emload's node lookup carried no upload server: {Snippet(response.Body)}");
        }

        string id = server.TryGetProperty("ID", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        string token = server.TryGetProperty("token", out JsonElement tokenElement) ? tokenElement.GetString() ?? string.Empty : string.Empty;

        return (new EmloadNode(uri, id, token), null);
    }

    private Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, EmloadSession session, EmloadNode node, string fileId)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["ui"] = session.Uid,
            ["ut"] = session.Ut,
            ["ud"] = session.Ud,
            ["ID"] = fileId,
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
                ctx.FilePath, node.Uri, "file", ctx.SpeedBudget, fields, headers, ctx.Cancellation);
    }

    /// <summary>
    /// Reads the upload reply into the share link the site's own script builds — <c>file/{token}</c>,
    /// the file's TOKEN rather than its id. Internal for testing.
    /// </summary>
    internal static (string? Link, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Emload rejected the upload (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"Emload's upload reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is { } reason)
        {
            return (null, reason == "disk"
                ? "Emload ran out of storage while taking the file."
                : $"Emload refused the upload: {reason}");
        }

        if (!root.TryGetProperty("file", out JsonElement file)
            || !file.TryGetProperty("token", out JsonElement token)
            || token.GetString() is not { Length: > 0 } fileToken)
        {
            return (null, $"Emload took the file but returned no link: {Snippet(response.Body)}");
        }

        return ($"{Host}/v2/file/{fileToken}", null);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (EmloadSession? session, string? error) = await SignInAsync(
            username,
            password,
            (url, json) => PostJsonAsync(handler, url, json, ApiHeaders(null), ct));

        return session is null
            ? new AccountCheckResult(false, AccountType.Free, error)
            : new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in to Emload.",
                SessionCookie: session.ToCookieHeader(),
                SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),

                // The email as typed: it is the identifier the next sign-in posts, so nothing the
                // account page might render may replace it.
                DerivedUsername: username);
    }

    /// <summary>
    /// Posts the site's own sign-in call. <c>robo</c> is its honeypot and travels as the literal
    /// <c>"__"</c> the page sends; there is no captcha.
    /// </summary>
    private static async Task<(EmloadSession? Session, string? Error)> SignInAsync(
        string? username,
        string? password,
        Func<string, string, Task<HttpResponseSnapshot>> postJson)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "Emload needs the account's email address and password.");
        }

        string json = JsonSerializer.Serialize(new
        {
            em = username,
            passw = password,
            robo = "__",
            ___uctmp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Tried twice, because this call was seen to fail once with a Cloudflare 520 — an origin
        // error, on a request identical to ones that worked either side of it. It is NOT a rate
        // limit: seven consecutive sign-ins minutes later all succeeded. Retrying is safe here in a
        // way it is not elsewhere in this file — a refused sign-in has created nothing — and the
        // alternative is an upload lost to a hiccup the host itself would forget about.
        (EmloadSession? Session, string? Error) result = default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseSnapshot response;
            try
            {
                response = await postJson(SignInUrl, json);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, "Emload sign-in failed: " + ex.Message);
            }

            result = ParseSignIn(response);
            if (result.Session is not null || !LooksTransient(response))
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>
    /// True when a reply says nothing about the credentials — an edge or origin error rather than the
    /// host's own JSON verdict. Only these are worth a second attempt; a rejection is final, and
    /// re-posting a password after one is how an account gets itself locked.
    /// </summary>
    internal static bool LooksTransient(HttpResponseSnapshot response)
    {
        if (response.StatusCode >= 500)
        {
            return true;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(response.Body);
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Reads the sign-in reply into the four values every later call needs. Internal for
    /// testing.</summary>
    internal static (EmloadSession? Session, string? Error) ParseSignIn(HttpResponseSnapshot response)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"Emload's sign-in reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (ReadErrorReason(root) is not null)
        {
            // The host's own wording is better than anything invented here: it distinguishes an
            // unknown address from a wrong password.
            string message = root.TryGetProperty("message", out JsonElement m) && m.GetString() is { Length: > 0 } text
                ? text
                : "Emload rejected the sign-in — check the email address and password.";
            return (null, message);
        }

        string? uid = ReadString(root, "uid");
        string? ut = ReadString(root, "ut");
        string? ud = ReadString(root, "ud");
        string? si = ReadString(root, "si");

        return uid is null || ut is null || ud is null || si is null
            ? (null, $"Emload signed in but issued an incomplete session: {Snippet(response.Body)}")
            : (new EmloadSession(uid, ut, ud, si), null);
    }

    /// <summary>Re-checks a stored session without the password by asking for the drive's folder tree —
    /// the lightest authenticated call the site makes, and the one that fails cleanly with
    /// <c>oauth</c>.</summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        if (EmloadSession.TryParse(sessionCookie) is not { } session)
        {
            return Expired();
        }

        try
        {
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            HttpResponseSnapshot response = await PostJsonAsync(
                handler,
                TreeUrl,
                JsonSerializer.Serialize(new { stamp, ___uctmp = stamp }),
                ApiHeaders(session),
                ct);

            if (response.StatusCode is >= 200 and < 300
                && ReadErrorReason(JsonDocument.Parse(response.Body).RootElement) is null)
            {
                return new AccountCheckResult(
                    true,
                    AccountType.Free,
                    "Signed in to Emload.",
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
            => new(false, AccountType.Free, "The saved Emload sign-in is no longer valid — sign in again.");
    }

    /// <summary>The API's error reason (<c>oauth</c>, <c>disk</c>, …), or null when the reply is a
    /// success. The envelope flags failure with <c>error:true</c> and never with the status code.</summary>
    private static string? ReadErrorReason(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error)
            || error.ValueKind is not JsonValueKind.True)
        {
            return null;
        }

        return root.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() ?? "unknown" : "unknown";
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.GetString() is { Length: > 0 } value ? value : null;

    private Task<HttpResponseSnapshot> PostJsonAsync(HttpHandler handler, string url, string json, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => _postJsonOverride is not null
            ? _postJsonOverride(url, json, headers)
            : handler.PostJsonAsync(url, json, headers, ct);

    /// <summary>
    /// Headers for the JSON API. <c>Authorization</c> is NOT a credential: the site's own
    /// <c>post()</c> reads a <c>__ha</c> cookie if it has one and otherwise sends twelve random
    /// characters (<c>ha || randit(12)</c>), and the server takes whatever arrives — so the app mints
    /// its own rather than pretending to hold a token.
    /// </summary>
    private static Dictionary<string, string> ApiHeaders(EmloadSession? session)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Accept"] = "application/json, text/plain, */*",
            ["Authorization"] = "Bearer " + NewNonce(),
            ["Origin"] = Host,
            ["Referer"] = Host + "/v2/",
        };

        if (session is not null)
        {
            headers["Cookie"] = session.ToCookieHeader();
        }

        return headers;
    }

    /// <summary>Twelve alphanumerics, as the site's own <c>randit(12)</c> mints. Internal for testing.</summary>
    internal static string NewNonce()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<char> chars = stackalloc char[12];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

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
    internal sealed record EmloadNode(string Uri, string Id, string Token);

    /// <summary>
    /// The four values a signed-in Emload session is made of. They travel as cookies the site's own
    /// JavaScript sets — the wire never carries a <c>Set-Cookie</c> for them — and three of them are
    /// ALSO multipart fields on the upload, which is why they are kept apart rather than stored as one
    /// opaque blob.
    /// </summary>
    internal sealed record EmloadSession(string Uid, string Ut, string Ud, string Si)
    {
        /// <summary>The jar as the API expects it, and as the account's stored credential.</summary>
        public string ToCookieHeader() => $"__uid={Uid}; __ut={Ut}; __ud={Ud}; __si={Si}";

        /// <summary>Reads a stored cookie header back into its four parts, or null when any is
        /// missing — a partial jar is exactly what earns the "oauth" refusal.</summary>
        public static EmloadSession? TryParse(string? cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return null;
            }

            Dictionary<string, string> parts = new(StringComparer.Ordinal);
            foreach (string piece in cookieHeader.Split(';'))
            {
                string[] kv = piece.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim() is { Length: > 0 } key && kv[1].Trim() is { Length: > 0 } value)
                {
                    parts[key] = value;
                }
            }

            return parts.TryGetValue("__uid", out string? uid)
                && parts.TryGetValue("__ut", out string? ut)
                && parts.TryGetValue("__ud", out string? ud)
                && parts.TryGetValue("__si", out string? si)
                ? new EmloadSession(uid, ut, ud, si)
                : null;
        }
    }
}
