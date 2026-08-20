// <copyright file="FileCatPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FileCat (filecat.net) — <b>account-only</b>, a small JSON REST API on its own
/// <c>api.filecat.net</c> host. Two calls plus the bytes:
/// <list type="number">
///   <item><b>Sign in.</b> <c>POST /user/signin</c> with <c>{"email","password"}</c> → 200 and
///   <c>Set-Cookie: SESS</c>. No captcha.</item>
///   <item><b>Ask where to put it.</b> <c>POST /upldreq</c> with
///   <c>{"file_size","file_name","file_path":null,"folder_id":null}</c> →
///   <c>{"link":"s7.filecat.net/upload/&lt;id&gt;","state":"satisfied","reject_reason":…}</c>.</item>
///   <item><b>Send it.</b> <c>POST https://&lt;link&gt;</c> — one multipart field named <c>file</c>,
///   <b>carrying the same session cookie</b>. The reply carries the share link and a delete code.</item>
/// </list>
/// <para>
/// <b>⚠ A REFUSAL ARRIVES INSIDE A 200.</b> Asking for more than the account may store answers
/// <c>200 {"link":null,"state":"rejected","reject_reason":"mfs","reject_msg":"File is too big"}</c>.
/// The status code is not the verdict — <c>state</c> is. Reading the transport alone would leave a
/// null link to be posted to.
/// </para>
/// <para>
/// <b>⚠ The link comes back with no scheme</b> (<c>s7.filecat.net/upload/…</c>), so it is prefixed
/// here. Posting it as-is would resolve against the API host.
/// </para>
/// <para>
/// <b>⚠ The storage node needs the session, and a capture makes it look as though it does not.</b>
/// That POST carries no <c>Authorization</c> header — but <c>SESS</c> is issued for
/// <c>domain=.filecat.net</c>, so the browser sends it to <c>sNN.filecat.net</c> as well. Reading the
/// capture for an auth header and concluding "no credential" costs a 403 that arrives <i>after</i>
/// the entire file has been transferred. <b>"No Authorization header" is not "no credential" — check
/// the Cookie, and check its domain.</b>
/// </para>
/// <para>
/// <b>Anonymous is refused outright</b> — <c>POST /upldreq</c> without a session answers
/// <c>403 {"message":"Access denied"}</c>. The cap is <b>2000 MiB per file</b>, found by asking
/// <c>upldreq</c> for progressively larger sizes until it refused (it costs nothing to ask, and no
/// page states the figure), against a 2 GiB total storage allowance the account reports itself.
/// </para>
/// <para>
/// <b>How this host was nearly missed:</b> a sweep probed <c>filecat.net</c> for the usual family
/// endpoints, got a small SPA shell, and filed it as unimplementable. <b>The whole API is on a
/// different subdomain</b> — nothing on the apex would ever have revealed it.
/// </para>
/// </summary>
public sealed class FileCatPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Site = "https://filecat.net";
    private const string Api = "https://api.filecat.net";
    private const string SignInUrl = Api + "/user/signin";
    private const string UploadRequestUrl = Api + "/upldreq";
    private const string UserUrl = Api + "/user/get";
    private const string StorageUrl = Api + "/v2/fs/storage";

    /// <summary>2000 MiB — the largest <c>file_size</c> <c>upldreq</c> accepts for a free account.</summary>
    private const long MaxFileSizeBytes = 2_097_152_000;

    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;

    public FileCatPipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON calls, the plain GETs and the multipart upload.</summary>
    internal FileCatPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? getOverride = null)
    {
        _postJsonOverride = postJsonOverride;
        _uploadOverride = uploadOverride;
        _getOverride = getOverride;
    }

    public string Name => "FileCat";

    /// <summary>Free downloads are captcha-gated: its live API answers a guest download request
    /// with captcha_needed true (api.filecat.net/dwnldreq, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Refused at source: an anonymous <c>upldreq</c> answers 403 "Access denied".</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed("FileCat has no anonymous upload — add an account for it in Account Manager.", null);
            yield break;
        }

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds FileCat's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === The session ===
        (string? session, string? authError) = await ResolveSessionAsync(ctx);
        if (session is null)
        {
            yield return new AttemptFailed(authError!, null);
            yield break;
        }

        // === Where to put it ===
        (string? node, string? requestError) = await RequestUploadAsync(ctx, session);
        if (node is null)
        {
            yield return new AttemptFailed(requestError!, null);
            yield break;
        }

        // === The bytes ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        // The node needs the session too. It carries no Authorization header — which is what made
        // the capture look credential-free — but SESS is set for `domain=.filecat.net`, so a browser
        // sends it to sNN.filecat.net as well. Without it the node takes the entire file and then
        // answers 403 "Access denied", measured.
        Task<HttpResponseSnapshot> uploadTask = _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, node, NodeHeaders(session), ctx.SpeedLimitProvider)
            : ctx.Handler.UploadMultipartAsync(ctx.FilePath, node, "file", null, NodeHeaders(session), ctx.SpeedLimitProvider, ctx.Cancellation);

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

        HttpResponseSnapshot response = await uploadTask;
        (string? link, string? deleteCode, string? uploadError) = ParseUploadResponse(response);
        if (link is null)
        {
            yield return new AttemptFailed(uploadError!, null);
            yield break;
        }

        // The only handle on the file besides the account's own file list, so it is logged rather
        // than dropped — as upload.ee's killcode and GigaFile's delete key are.
        if (deleteCode is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} delete code {deleteCode}");
        }

        yield return new TransferCompleted(link);
    }

    /// <summary>The stored session when there is one, else a fresh sign-in with the saved
    /// credentials.</summary>
    private async Task<(string? Session, string? Error)> ResolveSessionAsync(AttemptContext ctx)
    {
        if (NullIfWhiteSpace(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        return await SignInAsync(ctx.Credentials.Username, ctx.Credentials.Password,
            (url, json) => PostJsonAsync(ctx, url, json, null));
    }

    /// <summary>Static on purpose: the caller supplies <paramref name="post"/>, so this needs nothing
    /// from the instance — which is what lets both the upload path and the account check share it.</summary>
    private static async Task<(string? Session, string? Error)> SignInAsync(
        string? email, string? password, Func<string, string, Task<HttpResponseSnapshot>> post)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "FileCat needs the account's email address and password.");
        }

        HttpResponseSnapshot response;
        try
        {
            response = await post(SignInUrl, JsonSerializer.Serialize(new { email, password }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "FileCat sign-in failed: " + ex.Message);
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            // Its own wording for a bad pair is "Invalid email or password", which is worth passing on.
            return (null, $"FileCat rejected the sign-in: {ReadMessage(response.Body) ?? "check the email and password"}.");
        }

        return ReadSessionCookie(response) is { } session
            ? (session, null)
            : (null, "FileCat signed in but issued no session cookie.");
    }

    /// <summary>Pulls <c>SESS</c> out of the sign-in reply. Internal for testing.</summary>
    internal static string? ReadSessionCookie(HttpResponseSnapshot response)
    {
        foreach (string cookie in response.SetCookies)
        {
            if (cookie.StartsWith("SESS=", StringComparison.Ordinal))
            {
                return NullIfWhiteSpace(cookie.Split(';', 2)[0]["SESS=".Length..]);
            }
        }

        return null;
    }

    private async Task<(string? Node, string? Error)> RequestUploadAsync(AttemptContext ctx, string session)
    {
        string json = JsonSerializer.Serialize(new
        {
            file_size = ctx.FileSize,
            file_name = ctx.FileName,
            file_path = (string?)null,
            folder_id = (string?)null,
        });

        HttpResponseSnapshot response;
        try
        {
            response = await PostJsonAsync(ctx, UploadRequestUrl, json, session);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"FileCat upload request failed: {ex.Message}");
        }

        return ParseUploadRequest(response);
    }

    /// <summary>
    /// Reads the <c>upldreq</c> reply and returns the absolute node URL.
    /// <para>
    /// <b>A refusal rides inside a 200</b>: <c>state</c> is the verdict, not the status code. And the
    /// link arrives without a scheme, so it is made absolute here. Internal for testing.
    /// </para>
    /// </summary>
    internal static (string? Node, string? Error) ParseUploadRequest(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"FileCat wouldn't accept the upload (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): "
                + $"{ReadMessage(response.Body) ?? Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"FileCat's reply wasn't JSON: {Snippet(response.Body)}");
        }

        string? state = root.TryGetProperty("state", out JsonElement s) ? s.GetString() : null;
        if (!string.Equals(state, "satisfied", StringComparison.OrdinalIgnoreCase))
        {
            string? why = root.TryGetProperty("reject_msg", out JsonElement m) ? m.GetString() : null;
            return (null, string.IsNullOrWhiteSpace(why)
                ? $"FileCat refused the upload ({state ?? "no state"}): {Snippet(response.Body)}"
                : $"FileCat refused the upload: {why}");
        }

        string? link = root.TryGetProperty("link", out JsonElement l) ? l.GetString() : null;
        if (string.IsNullOrWhiteSpace(link))
        {
            return (null, $"FileCat accepted the upload but named no node: {Snippet(response.Body)}");
        }

        // "s7.filecat.net/upload/123" — no scheme, so it would otherwise resolve against the API host.
        return (link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : "https://" + link, null);
    }

    /// <summary>Reads the node's reply: the share link, and the delete code it hands back once.
    /// Internal for testing.</summary>
    internal static (string? Link, string? DeleteCode, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"FileCat's storage node refused the file (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;
            string? link = root.TryGetProperty("link", out JsonElement l) ? l.GetString() : null;
            string? del = root.TryGetProperty("cd_uid", out JsonElement d) ? d.GetString() : null;

            return string.IsNullOrWhiteSpace(link)
                ? (null, null, $"FileCat took the file but returned no link: {Snippet(response.Body)}")
                : (link, NullIfWhiteSpace(del), null);
        }
        catch (JsonException)
        {
            return (null, null, $"FileCat's storage node didn't answer with JSON: {Snippet(response.Body)}");
        }
    }

    private Task<HttpResponseSnapshot> PostJsonAsync(AttemptContext ctx, string url, string json, string? session)
        => _postJsonOverride is not null
            ? _postJsonOverride(url, json, ApiHeaders(session))
            : ctx.Handler.PostJsonAsync(url, json, ApiHeaders(session), ctx.Cancellation);

    /// <summary>Headers for the API host. <paramref name="session"/> rides as the <c>SESS</c> cookie,
    /// which is how the site itself authenticates every call.</summary>
    private static Dictionary<string, string> ApiHeaders(string? session)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Site,
            ["Referer"] = Site + "/",
            ["Accept"] = "application/json, text/plain, */*",
        };

        if (session is not null)
        {
            headers["Cookie"] = "SESS=" + session;
        }

        return headers;
    }

    /// <summary>
    /// Headers for the storage node. It <b>does</b> need the session: <c>SESS</c> is issued for
    /// <c>domain=.filecat.net</c>, so a browser sends it to <c>sNN.filecat.net</c> too. The request
    /// carries no <c>Authorization</c> header, which is exactly what makes a capture look
    /// credential-free — and sending nothing earns a 403 only after the whole file has gone up.
    /// </summary>
    private static Dictionary<string, string> NodeHeaders(string session) => new(StringComparer.Ordinal)
    {
        ["Origin"] = Site,
        ["Referer"] = Site + "/",
        ["Cookie"] = "SESS=" + session,
    };

    /// <summary>
    /// Signs in and keeps the session. The email is returned exactly as typed: it is the identifier
    /// the next sign-in posts, so nothing read off the account may replace it.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? session, string? error) = await SignInAsync(username, password,
            (url, json) => _postJsonOverride is not null
                ? _postJsonOverride(url, json, ApiHeaders(null))
                : handler.PostJsonAsync(url, json, ApiHeaders(null), ct));

        if (session is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error);
        }

        (long? used, long? total) = await ReadStorageAsync(handler, session, ct);

        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to FileCat.",
            SessionCookie: session,
            SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(30),
            DerivedUsername: username,
            StorageUsedBytes: used,
            StorageQuotaBytes: total);
    }

    /// <summary>Re-checks the stored session without a password, and refreshes the storage figures
    /// while it is there.</summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        try
        {
            HttpResponseSnapshot me = _getOverride is not null
                ? await _getOverride(UserUrl, ApiHeaders(sessionCookie))
                : await handler.GetSnapshotAsync(UserUrl, ApiHeaders(sessionCookie), ct);

            if (me.StatusCode is >= 200 and < 300 && me.Body.Contains("\"id\"", StringComparison.Ordinal))
            {
                (long? used, long? total) = await ReadStorageAsync(handler, sessionCookie, ct);
                return new AccountCheckResult(
                    true,
                    AccountType.Free,
                    "Signed in to FileCat.",
                    SessionCookie: sessionCookie,
                    SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(30),
                    StorageUsedBytes: used,
                    StorageQuotaBytes: total);
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

        return new AccountCheckResult(false, AccountType.Free, "The saved FileCat sign-in is no longer valid — sign in again.");
    }

    /// <summary>The account's own <c>used</c> / <c>total</c>. Never worth failing a good sign-in
    /// over, so every failure here simply yields no figures.</summary>
    private async Task<(long? Used, long? Total)> ReadStorageAsync(HttpHandler handler, string session, CancellationToken ct)
    {
        try
        {
            HttpResponseSnapshot response = _getOverride is not null
                ? await _getOverride(StorageUrl, ApiHeaders(session))
                : await handler.GetSnapshotAsync(StorageUrl, ApiHeaders(session), ct);

            return ParseStorage(response.Body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Reads <c>{"used":…,"total":…}</c>. Internal for testing.</summary>
    internal static (long? Used, long? Total) ParseStorage(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            long? used = root.TryGetProperty("used", out JsonElement u) && u.TryGetInt64(out long uv) ? uv : null;
            long? total = root.TryGetProperty("total", out JsonElement t) && t.TryGetInt64(out long tv) ? tv : null;
            return (used, total);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadMessage(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            foreach (string key in new[] { "message", "email", "password" })
            {
                if (root.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                {
                    return NullIfWhiteSpace(v.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — the caller falls back to a snippet.
        }

        return null;
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
}
