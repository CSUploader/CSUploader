// <copyright file="IcerBoxPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// IcerBox (icerbox.com) upload pipeline. Unlike the XFileSharing family, icerbox is a clean
/// JSON REST API (an Angular SPA over <c>https://icerbox.com/api/v1</c>) with a plain
/// email+password login — no captcha, no cookie scraping, no WebView. Verified end-to-end
/// against the live site 2026-06-24 with a free account.
/// <list type="number">
///   <item><b>Login.</b> <c>POST /api/v1/auth/login</c> with JSON <c>{keep_me, email, password}</c>
///   → <c>{"token":"&lt;JWT&gt;"}</c> (≈1 h lifetime). Every other call sends
///   <c>Authorization: Bearer &lt;JWT&gt;</c>. The JWT is cached per credentials id and re-fetched
///   when a call returns 401/403. (Brute-force protection can demand a reCAPTCHA — surfaced as a
///   429 with a clear message — but a normal login doesn't.)</item>
///   <item><b>Discover the upload node.</b> <c>GET /api/v1/upload/server</c> →
///   <c>{"data":{"domain":"sNN.icerbox.com","port":8443,"upload":true}}</c>. Assigned per request.</item>
///   <item><b>Upload.</b> The node runs blueimp jQuery-File-Upload: a multipart POST to
///   <c>https://{domain}:{port}/</c> with the file under <c>files[]</c> and the Bearer header →
///   <c>{"files":[{"id":"&lt;code&gt;",...}]}</c>. The share link is <c>https://icerbox.com/&lt;code&gt;</c>.</item>
/// </list>
/// Classic username(email)/password account — NOT an API-key/WebView hoster. No declared per-file
/// size cap is exposed, so <see cref="MaxFileSize"/> is null and an oversized file surfaces the
/// server's own rejection rather than a guessed client-side limit.
/// </summary>
public sealed class IcerBoxPipeline : IFileHosterPipeline
{
    private const string ApiBase = "https://icerbox.com/api/v1";
    private const string LoginUrl = ApiBase + "/auth/login";
    private const string AccountUrl = ApiBase + "/user/account";
    private const string UploadServerUrl = ApiBase + "/upload/server";
    private const string SiteOrigin = "https://icerbox.com";
    private const string DownloadBase = "https://icerbox.com/";

    // The blueimp upload field name (the SPA's default) and the SPA's UI language header.
    private const string FileFieldName = "files[]";
    private const string AppLang = "en_US";

    private static readonly IReadOnlyDictionary<string, string> NoExtraFields =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<int, IcerBoxAuthState> _authByCredentialsId = new();

    // One login at a time per credentials id, so N parallel uploads for the same account don't
    // fire N concurrent logins (and risk tripping the brute-force/captcha gate). The leader logs
    // in and writes the cache; followers reuse it without an extra round-trip.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, string, Task<HttpResponseSnapshot>>? _loginOverride;
    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    /// <summary>Production ctor — uses the <see cref="AttemptContext.Handler"/> for HTTP.</summary>
    public IcerBoxPipeline()
    {
    }

    /// <summary>Test ctor — drives the login POST and the api/v1 GETs from canned responses
    /// (no upload). Used by the parse/verify tests.</summary>
    internal IcerBoxPipeline(
        Func<string, string, HttpResponseSnapshot> loginOverride,
        Func<string, HttpResponseSnapshot> getOverride)
    {
        _loginOverride = (url, body) => Task.FromResult(loginOverride(url, body));
        _getOverride = url => Task.FromResult(getOverride(url));
    }

    /// <summary>Test ctor — also substitutes the multipart upload so the full RunAsync flow
    /// (login → upload server → upload → parse) can run without the network.</summary>
    internal IcerBoxPipeline(
        Func<string, string, HttpResponseSnapshot> loginOverride,
        Func<string, HttpResponseSnapshot> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : this(loginOverride, getOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "IcerBox";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No per-file cap is advertised by the API; the server enforces its own limit and an
    /// oversized file comes back as a rejection rather than a guessed client-side block.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct; // the pipeline uses ctx.Cancellation, matching the other hosters.

        // === Auth (cached per credentials id) ===
        IcerBoxAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out IcerBoxAuthState? cached))
        {
            auth = cached;
        }
        else
        {
            (IcerBoxAuthState? gated, bool didLogin, string? loginError) = await EnsureAuthAsync(ctx);
            if (didLogin)
            {
                yield return new AuthStarted();
            }

            if (gated is null)
            {
                if (didLogin)
                {
                    yield return new AuthFailed(loginError ?? "login failed");
                }

                yield return new AttemptFailed(loginError ?? "icerbox login failed", null);
                yield break;
            }

            if (didLogin)
            {
                yield return new AuthSucceeded();
            }

            auth = gated;
        }

        // === Discover the upload node ===
        (string? uploadEndpoint, string? serverError, bool serverAuthExpired) = await GetUploadServerAsync(ctx, auth);
        if (serverAuthExpired)
        {
            InvalidateToken(ctx.Credentials.Id, auth);
            yield return new AuthFailed("icerbox session expired");
            yield return new AttemptFailed("icerbox session expired — retry will re-authenticate", null);
            yield break;
        }

        if (uploadEndpoint is null)
        {
            yield return new AttemptFailed(serverError ?? "icerbox upload/server failed", null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // === Upload the bytes — bridge HttpHandler.UploadProgress to TransferProgress events ===
        // The upload runs concurrently; progress callbacks write into an unbounded channel this
        // iterator drains. Can't yield from inside the event handler, hence the channel.
        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadEndpoint, auth.Token);
        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Don't pass the token here: when it fires, UploadAsync throws, the ContinueWith completes
        // the writer, and ReadAllAsync drains naturally. Passing it would make ReadAllAsync throw
        // before the channel is fully drained.
        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): a mid-send
        // abort or connect-phase failure arrives as a safe-to-retry UploadBodyTransferException, and
        // re-running the whole pipeline discovers a FRESH node — the node never double-creates
        // because the body never finished sending. A user cancel surfaces as OperationCanceledException
        // (classified by AttemptRunner). A SERVER VERDICT never throws (UploadMultipartAsync returns
        // the snapshot), so it parses below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? uploadError, bool uploadAuthExpired) = ParseUploadResponse(uploadResponse);
        if (uploadAuthExpired)
        {
            InvalidateToken(ctx.Credentials.Id, auth);
            yield return new AuthFailed("icerbox session expired");
            yield return new AttemptFailed("icerbox session expired — retry will re-authenticate", null);
            yield break;
        }

        if (url is null)
        {
            yield return new AttemptFailed(uploadError ?? "icerbox upload failed", null);
            yield break;
        }

        yield return new TransferCompleted(url);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey; // icerbox is username(email)/password — no API key.
        _ = proxy;  // the handler already routes through the chosen proxy.

        (string? token, string? loginError) = await LoginAsync(handler, username, password, ct);
        if (token is null)
        {
            return new AccountCheckResult(false, AccountType.Free, loginError ?? "icerbox login failed");
        }

        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(handler, AccountUrl, token, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "icerbox account read failed: " + ex.Message);
        }

        return ParseAccount(snap);
    }

    /// <summary>
    /// Acquires the per-credentials gate, double-checks the cache, and either logs in (leader) or
    /// returns the cached state (followers). <c>DidLogin</c> is true only for the leader, so RunAsync
    /// emits the Auth* events once per account rather than per file.
    /// </summary>
    private async Task<(IcerBoxAuthState? Auth, bool DidLogin, string? Error)> EnsureAuthAsync(AttemptContext ctx)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation);
        try
        {
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out IcerBoxAuthState? cached))
            {
                return (cached, false, null);
            }

            (string? token, string? error) = await LoginAsync(ctx.Handler, ctx.Credentials.Username ?? string.Empty, ctx.Credentials.Password ?? string.Empty, ctx.Cancellation);
            if (token is null)
            {
                return (null, true, error);
            }

            IcerBoxAuthState state = new(token);
            _authByCredentialsId[ctx.Credentials.Id] = state;
            return (state, true, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drops the cached token, but only if it's still the one we used — so a concurrent
    /// upload's freshly-acquired token isn't clobbered by this attempt's stale-token cleanup.</summary>
    private void InvalidateToken(int credentialsId, IcerBoxAuthState used)
    {
        if (_authByCredentialsId.TryGetValue(credentialsId, out IcerBoxAuthState? current)
            && ReferenceEquals(current, used))
        {
            _authByCredentialsId.TryRemove(credentialsId, out _);
        }
    }

    private async Task<(string? Token, string? Error)> LoginAsync(HttpHandler handler, string email, string password, CancellationToken ct)
    {
        // JsonSerializer escapes the credentials safely and preserves the {keep_me, email, password}
        // order the SPA sends.
        string body = JsonSerializer.Serialize(new { keep_me = true, email, password });
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["AppLang"] = AppLang,
            ["Origin"] = SiteOrigin,
            ["Accept"] = "application/json",
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = _loginOverride is not null
                ? await _loginOverride(LoginUrl, body)
                : await handler.PostJsonAsync(LoginUrl, body, headers, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "icerbox login request failed: " + ex.Message);
        }

        return ParseLoginToken(snap);
    }

    private static (string? Token, string? Error) ParseLoginToken(HttpResponseSnapshot snap)
    {
        // Brute-force protection demands a reCAPTCHA, which we can't solve headlessly. Surface it
        // plainly rather than as an opaque parse failure.
        if (snap.StatusCode == 429)
        {
            return (null, "icerbox is temporarily rate-limiting logins (a captcha is required). Wait a little and try again.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("token", out JsonElement tokenEl)
                && tokenEl.ValueKind == JsonValueKind.String)
            {
                string? token = tokenEl.GetString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return (token, null);
                }
            }

            return (null, $"icerbox login failed: {ExtractMessage(root) ?? Snippet(snap.Body)} (HTTP {snap.StatusCode})");
        }
        catch (JsonException)
        {
            return (null, $"icerbox login returned an unexpected response (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }
    }

    private async Task<(string? Endpoint, string? Error, bool AuthExpired)> GetUploadServerAsync(AttemptContext ctx, IcerBoxAuthState auth)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx.Handler, UploadServerUrl, auth.Token, ctx.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "icerbox upload/server request failed: " + ex.Message, false);
        }

        if (snap.StatusCode is 401 or 403)
        {
            return (null, null, true);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                // Block ONLY on an explicit upload:false (don't false-block when the field is absent).
                bool uploadDisabled = data.TryGetProperty("upload", out JsonElement up) && up.ValueKind == JsonValueKind.False;
                if (uploadDisabled)
                {
                    return (null, "icerbox reports uploading isn't available for this account right now.", false);
                }

                string? domain = data.TryGetProperty("domain", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                int? port = data.TryGetProperty("port", out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pv) ? pv : null;
                if (!string.IsNullOrWhiteSpace(domain) && port is int portValue)
                {
                    return ($"https://{domain}:{portValue.ToString(CultureInfo.InvariantCulture)}/", null, false);
                }
            }
        }
        catch (JsonException)
        {
            // fall through to the shared error below
        }

        return (null, $"icerbox didn't return an upload server (HTTP {snap.StatusCode}): {Snippet(snap.Body)}", false);
    }

    private Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string endpoint, string token)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + token,
            ["Origin"] = SiteOrigin,
        };

        if (_uploadOverride is not null)
        {
            return _uploadOverride(ctx.FilePath, endpoint, NoExtraFields, headers, ctx.SpeedLimitProvider);
        }

        return ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            endpoint,
            fileFieldName: FileFieldName,
            extraFields: NoExtraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is <c>{"files":[{"id":"&lt;code&gt;",...}]}</c> → the share link
    /// <c>https://icerbox.com/&lt;code&gt;</c>. A 401/403 signals an expired token (retryable); any
    /// other shape surfaces the server's message/body so size/policy rejections are legible.
    /// </summary>
    private static (string? Url, string? Error, bool AuthExpired) ParseUploadResponse(HttpResponseSnapshot snap)
    {
        if (snap.StatusCode is 401 or 403)
        {
            return (null, null, true);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("files", out JsonElement files)
                && files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0)
            {
                JsonElement first = files[0];
                string? id = first.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return (DownloadBase + id, null, false);
                }
            }

            return (null, $"icerbox upload failed: {ExtractMessage(root) ?? Snippet(snap.Body)} (HTTP {snap.StatusCode})", false);
        }
        catch (JsonException)
        {
            return (null, $"icerbox upload returned an unexpected response (HTTP {snap.StatusCode}): {Snippet(snap.Body)}", false);
        }
    }

    private static AccountCheckResult ParseAccount(HttpResponseSnapshot snap)
    {
        if (snap.StatusCode is < 200 or >= 300)
        {
            return new AccountCheckResult(false, AccountType.Free, $"icerbox account check failed (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return new AccountCheckResult(false, AccountType.Free, $"icerbox account check: unexpected response: {Snippet(snap.Body)}");
            }

            string? email = data.TryGetProperty("email", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            bool hasPremium = data.TryGetProperty("has_premium", out JsonElement hp) && hp.ValueKind == JsonValueKind.True;
            DateTime? expiry = ParsePremiumExpiry(data);

            AccountType type = hasPremium ? AccountType.Premium : AccountType.Free;
            string message = hasPremium
                ? (expiry is { } exp ? string.Format(CultureInfo.InvariantCulture, "Premium until {0:yyyy-MM-dd}", exp) : "Premium")
                : "Free";

            return new AccountCheckResult(true, type, message, expiry, DerivedUsername: string.IsNullOrEmpty(email) ? null : email);
        }
        catch (JsonException)
        {
            return new AccountCheckResult(false, AccountType.Free, $"icerbox account check returned an unexpected response: {Snippet(snap.Body)}");
        }
    }

    /// <summary>Best-effort premium expiry from the account's <c>premium</c> field. The free-tier
    /// shape is <c>null</c>; a premium account's exact shape is unconfirmed, so accept either an ISO
    /// date string or a <c>{date:"…"}</c> object and fall back to null (the AccountType is what
    /// matters; expiry is cosmetic).</summary>
    private static DateTime? ParsePremiumExpiry(JsonElement data)
    {
        if (!data.TryGetProperty("premium", out JsonElement premium))
        {
            return null;
        }

        string? raw = premium.ValueKind switch
        {
            JsonValueKind.String => premium.GetString(),
            JsonValueKind.Object when premium.TryGetProperty("date", out JsonElement dt) && dt.ValueKind == JsonValueKind.String => dt.GetString(),
            _ => null,
        };

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? parsed
            : null;
    }

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, string token, CancellationToken ct)
    {
        if (_getOverride is not null)
        {
            return _getOverride(url);
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + token,
            ["AppLang"] = AppLang,
            ["Accept"] = "application/json",
        };
        return handler.GetSnapshotAsync(url, headers, ct);
    }

    /// <summary>Pulls a human-readable error out of an icerbox error envelope, trying the
    /// common <c>message</c>/<c>error</c> string fields. Null when neither is present.</summary>
    private static string? ExtractMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in (ReadOnlySpan<string>)["message", "error"])
        {
            if (root.TryGetProperty(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? value = el.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 300;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
