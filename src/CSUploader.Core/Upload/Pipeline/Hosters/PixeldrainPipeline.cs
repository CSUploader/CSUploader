// <copyright file="PixeldrainPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Pixeldrain (pixeldrain.com) upload pipeline — account-only (email/username + password), no captcha.
/// Verified against a live capture (2026-07-04) plus live endpoint probing. Pixeldrain removed
/// anonymous upload: an unauthenticated <c>PUT /api/file/&lt;name&gt;</c> returns HTTP 401
/// <c>{"success":false,"value":"authentication_required",…}</c>.
/// <para>
/// Pixeldrain's REST API authenticates with an <b>API key</b> passed as the password of HTTP Basic Auth
/// (<c>Authorization: Basic base64(":"+key)</c>). Crucially, the key returned by
/// <c>POST /api/user/login</c> (its <c>auth_key</c>, also set as the <c>pd_auth_key</c> cookie) <b>is</b>
/// such an API key — "the same kind of key" the account's API-keys page issues. So this pipeline obtains
/// a key once at sign-in, persists it (<see cref="AccountCheckResult.ApiKey"/> → the DTO), and uploads
/// with Basic Auth. It falls back to the original login-cookie flow when no key is stored yet (older
/// accounts) or a stored key is rejected.
/// </para>
/// <list type="number">
///   <item><b>Sign-in / key.</b> <c>POST /api/user/login</c> (<c>username</c> — username OR email —
///   <c>password</c>, <c>app_name</c>) returns HTTP 201, a JSON <c>auth_key</c>, and
///   <c>Set-Cookie: pd_auth_key=&lt;key&gt;</c>. That key is the API key, cached per credentials id and
///   surfaced to the Settings layer for persistence. A stored key is re-validated cheaply with
///   <c>GET /api/user/session</c> (Basic Auth) instead of re-logging-in.</item>
///   <item><b>Upload.</b> <c>PUT /api/file/&lt;url-encoded-filename&gt;</c> with either
///   <c>Authorization: Basic base64(":"+key)</c> (stored API key) or <c>Cookie: pd_auth_key=&lt;key&gt;</c>
///   (fallback), raw file bytes as the body, Content-Type from the extension. HTTP 201
///   <c>{"id":"&lt;id&gt;"}</c>; share link <c>https://pixeldrain.com/u/&lt;id&gt;</c>.</item>
/// </list>
/// No hashing. The streamed raw PUT (progress + retryable mid-send reclassification) is
/// <see cref="HttpHandler.UploadPutAsync"/>, exactly as Storage.to uses it.
/// </summary>
public sealed class PixeldrainPipeline : IFileHosterPipeline
{
    private const string Host = "https://pixeldrain.com";
    private const string LoginUrl = Host + "/api/user/login";
    private const string SessionUrl = Host + "/api/user/session";
    private const string FileApiPrefix = Host + "/api/file/";
    private const string PublicUrlPrefix = Host + "/u/";

    // The auth cookie pixeldrain's login sets; its value is the account's API key.
    private const string AuthCookieName = "pd_auth_key";

    // Label shown next to the key/session on pixeldrain's API-keys page.
    private const string AppName = "CSUploader";

    // pd_auth_key cached per credentials id (the fallback login-cookie path). One login at a time per id
    // so a batch of N files does ONE login, not N (same shape as Upstore's usid cache).
    private readonly ConcurrentDictionary<int, string> _authKeyByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public PixeldrainPipeline()
    {
    }

    /// <summary>Test ctor — stubs the login POST, the raw PUT upload, and (optionally) the key-validation
    /// GET so the whole sign-in → key → upload orchestration runs without the network or a real file.</summary>
    internal PixeldrainPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot>? getOverride = null)
    {
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _uploadOverride = uploadOverride;
        _getOverride = getOverride is null ? null : (url, headers) => Task.FromResult(getOverride(url, headers));
    }

    public string Name => "Pixeldrain";

    /// <summary>From its own about page (read 2026-08-12): "Files will be removed if they have not
    /// been accessed for 60 days", every tier; a download resets the timer (at most once per 24
    /// hours, and only when more than a tenth of the file is fetched).</summary>
    public FileRetention RetentionFor(FileHosterLoginDto credentials)
        => FileRetention.DaysAfterLastDownload(60);

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No hard per-file cap declared — pixeldrain's limits are generous and the server rejects an
    /// oversized file with a clear error, which surfaces as a normal upload failure.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Pixeldrain removed anonymous upload — an account is required.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        string? apiKey = NullIfWhiteSpace(ctx.Credentials.ApiKey);

        string? resultId = null;
        string? finalError = null;
        bool apiKeyTried = false;
        bool transferStarted = false;
        string? cookieKeyUsed = null;

        // Try the stored API key (Basic auth) first, then fall back to the login-cookie path — the
        // original, capture-verified flow, which is also the sole path for accounts with no stored key.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Dictionary<string, string> headers;
            bool usingApiKey = apiKey is not null && !apiKeyTried;
            if (usingApiKey)
            {
                apiKeyTried = true;
                headers = UploadHeaders("Authorization", "Basic " + BasicToken(apiKey!));
            }
            else
            {
                (string? loginKey, string? loginError) = await EnsureAuthKeyAsync(ctx.Handler, ctx.Credentials, ctx.Cancellation);
                if (loginKey is null)
                {
                    finalError = loginError;
                    break;
                }

                cookieKeyUsed = loginKey;
                headers = UploadHeaders("Cookie", AuthCookieName + "=" + loginKey);
            }

            // Signal the transfer once we actually have auth and are about to send bytes (a login failure
            // above breaks out before this — no premature TransferStarted).
            if (!transferStarted)
            {
                transferStarted = true;
                yield return new TransferStarted(ctx.FileSize);
            }

            // Bridge HttpHandler.UploadProgress -> TransferProgress via a channel (can't yield from inside
            // the event handler) — same pattern as the other streaming pipelines.
            Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void OnProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += OnProgress;

            Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, headers);
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
            // pixeldrain has committed no file, so it arrives as a retryable UploadBodyTransferException
            // and a whole-pipeline retry re-uploads cleanly. A server verdict does NOT throw.
            HttpResponseSnapshot resp = await uploadTask;

            (string? id, bool authExpired, string? err) = ParseUploadResponse(resp);
            if (id is not null)
            {
                resultId = id;
                break;
            }

            finalError = err;
            if (!authExpired)
            {
                break; // a real rejection (too large, etc.) — other auth won't help
            }

            if (usingApiKey)
            {
                continue; // stored key rejected → fall back to the login-cookie path on the next iteration
            }

            // Login-cookie path rejected → drop the stale cached key so a later attempt re-logs-in.
            if (cookieKeyUsed is not null)
            {
                ((ICollection<KeyValuePair<int, string>>)_authKeyByCredId)
                    .Remove(new KeyValuePair<int, string>(ctx.Credentials.Id, cookieKeyUsed));
            }

            break;
        }

        if (resultId is null)
        {
            yield return new AttemptFailed(finalError ?? "Pixeldrain upload failed.", null);
            yield break;
        }

        yield return new TransferCompleted(PublicUrlPrefix + resultId);
    }

    /// <summary>
    /// Verifies a Pixeldrain account and returns its API key for persistence. A key already on the DTO is
    /// re-validated cheaply (no new session); otherwise a login mints one (the login's <c>auth_key</c> is
    /// itself an API key).
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy;

        // Reuse a still-valid stored key so a Refresh doesn't spawn a new session every time.
        string? existing = NullIfWhiteSpace(apiKey);
        if (existing is not null && await ValidateApiKeyAsync(handler, existing, ct))
        {
            return new AccountCheckResult(true, AccountType.Free, "Signed in (Free)", DerivedUsername: username, ApiKey: existing);
        }

        // No key (or it's been revoked/expired) → log in to obtain one; the auth_key IS an API key.
        (string? authKey, string? error) = await LoginAsync(handler, username, password, ct);
        return authKey is null
            ? new AccountCheckResult(false, AccountType.Free, error ?? "Pixeldrain login failed.")
            : new AccountCheckResult(true, AccountType.Free, "Signed in (Free)", DerivedUsername: username, ApiKey: authKey);
    }

    /// <summary>Confirms an API key still works via <c>GET /api/user/session</c> (Basic Auth): 2xx = valid.
    /// Any failure (401, transport) collapses to false so the caller re-logs-in.</summary>
    private async Task<bool> ValidateApiKeyAsync(HttpHandler handler, string apiKey, CancellationToken ct)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Authorization"] = "Basic " + BasicToken(apiKey),
        };

        try
        {
            HttpResponseSnapshot snap = await GetSnapshotAsync(handler, SessionUrl, headers, ct);
            return snap.StatusCode is >= 200 and < 300;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Returns the cached pd_auth_key for the account, logging in once (gated per credentials id)
    /// on a cache miss.</summary>
    private async Task<(string? AuthKey, string? Error)> EnsureAuthKeyAsync(HttpHandler handler, FileHosterLoginDto creds, CancellationToken ct)
    {
        int id = creds.Id;
        if (_authKeyByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_authKeyByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? authKey, string? error) = await LoginAsync(handler, creds.Username, creds.Password, ct);
            if (authKey is null)
            {
                return (null, error);
            }

            _authKeyByCredId[id] = authKey;
            return (authKey, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>POSTs the login form and pulls <c>pd_auth_key</c> (the API key) out of the response's
    /// <c>Set-Cookie</c>. A wrong username/password returns a JSON error envelope and no cookie.</summary>
    private async Task<(string? AuthKey, string? Error)> LoginAsync(HttpHandler handler, string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, "Pixeldrain account needs a username (or email) and password.");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["username"] = username,
            ["password"] = password,
            ["app_name"] = AppName,
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(handler, LoginUrl, form, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "Pixeldrain login request failed: " + ex.Message);
        }

        string? authKey = ExtractCookieValue(snap.SetCookies, AuthCookieName) ?? TryReadStringField(snap.Body, "auth_key");
        if (!string.IsNullOrEmpty(authKey))
        {
            return (authKey, null);
        }

        string? apiMessage = TryReadApiMessage(snap.Body);
        return (null, apiMessage is not null
            ? "Pixeldrain login failed: " + apiMessage
            : $"Pixeldrain login failed — check the username and password (HTTP {snap.StatusCode}).");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, IReadOnlyDictionary<string, string> headers)
    {
        string url = FileApiPrefix + Uri.EscapeDataString(ctx.FileName);

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, url, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadPutAsync(
            ctx.FilePath,
            url,
            contentType: MimeTypeGuesser.Guess(ctx.FilePath),
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 201 with JSON <c>{"id":"&lt;id&gt;"}</c>. The middle flag is true when the failure
    /// is an auth error (HTTP 401 / <c>value:"authentication_required"</c>) so the caller can try the
    /// other auth mechanism / drop the stale key. Any other error surfaces with the API message.
    /// </summary>
    private static (string? Id, bool AuthExpired, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is >= 200 and < 300)
        {
            string? id = TryReadStringField(response.Body, "id");
            if (!string.IsNullOrEmpty(id))
            {
                return (id, false, null);
            }
        }

        bool authExpired = response.StatusCode == 401
            || string.Equals(TryReadStringField(response.Body, "value"), "authentication_required", StringComparison.Ordinal);

        string? message = TryReadApiMessage(response.Body);
        return (null, authExpired, $"Pixeldrain upload failed (HTTP {response.StatusCode}): {message ?? Snippet(response.Body)}");
    }

    private static Dictionary<string, string> UploadHeaders(string authName, string authValue) => new(StringComparer.Ordinal)
    {
        [authName] = authValue,
        ["Origin"] = Host,
        ["Referer"] = Host + "/user/",
    };

    /// <summary>HTTP Basic Auth token for pixeldrain: the API key in the password field, empty username —
    /// <c>base64(":"+key)</c>.</summary>
    private static string BasicToken(string apiKey) => Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + apiKey));

    private static string? NullIfWhiteSpace(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Reads pixeldrain's error text from a <c>{"success":false,"value":"…","message":"…"}</c>
    /// envelope — the <c>message</c> (falling back to <c>value</c>). Null when the body isn't that shape.</summary>
    private static string? TryReadApiMessage(string body)
        => TryReadStringField(body, "message") ?? TryReadStringField(body, "value");

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

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getOverride is not null ? _getOverride(url, headers) : handler.GetSnapshotAsync(url, headers, ct);

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, CancellationToken ct)
        => _postFormOverride is not null ? _postFormOverride(url, form) : handler.PostFormAsync(url, form, ct);

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
