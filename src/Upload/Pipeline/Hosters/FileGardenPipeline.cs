// <copyright file="FileGardenPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// File Garden (filegarden.com) upload pipeline — account-only (email + password), no captcha on login.
/// Verified against a live capture (2026-07-04) plus live endpoint probing. The site's REST API lives on
/// <c>api.filegarden.com</c>; the browser signup is behind a Cloudflare Turnstile challenge but
/// <c>POST /token</c> (login) is NOT, so this runs in C# without a WebView.
/// <list type="number">
///   <item><b>Login.</b> <c>POST https://api.filegarden.com/token</c> with a JSON body
///   <c>{"connection":"password &lt;base64(password)&gt;","email":"…"}</c>. Success is HTTP 201 with
///   <c>{"id":"&lt;userId&gt;","token":"…"}</c> and a <c>Set-Cookie: auth=…</c> session cookie. Errors are
///   JSON <c>{"error":"…"}</c> (e.g. <c>422 {"error":"That email is not verified.","unverified":true}</c>
///   for an unconfirmed account). The (auth cookie, userId) pair is cached per credentials id.</item>
///   <item><b>Upload.</b> <c>POST https://api.filegarden.com/users/&lt;userId&gt;/pipe</c> with
///   <c>Cookie: auth=…</c>, an <c>X-Data</c> header carrying url-encoded
///   <c>{"parent":null,"name":"&lt;filename&gt;"}</c>, Content-Type <c>application/octet-stream</c>, and the
///   raw file bytes as the body. HTTP 201 <c>{"id":"…","path":"&lt;path&gt;",…}</c>; the public link is
///   <c>https://filegarden.com/&lt;userId&gt;/&lt;path&gt;</c>.</item>
/// </list>
/// No hashing. The streamed raw POST (progress + retryable mid-send reclassification) is
/// <see cref="HttpHandler.UploadFileBodyAsync"/>, the method-parameterized raw-body primitive.
/// </summary>
public sealed class FileGardenPipeline : IFileHosterPipeline
{
    private const string Host = "https://filegarden.com";

    // File Garden's hard per-file limit (its web app: "we don't support uploading files above 100 MiB").
    private const long MaxFileSizeBytes = 100L * 1024 * 1024; // 104,857,600

    // Public files are served from the SHORT domain under a per-user "garden" id (see GardenId) — NOT
    // filegarden.com/<userId>. e.g. https://file.garden/akjxPE7rAW98wmwC/<filename>.
    private const string PublicHost = "https://file.garden";
    private const string ApiBase = "https://api.filegarden.com";
    private const string TokenUrl = ApiBase + "/token";
    private const string AuthCookieName = "auth";

    // Serialize the X-Data JSON with raw UTF-8 (not \uXXXX) so EncodeUri percent-encodes it exactly the
    // way the browser does — see EncodeUri.
    private static readonly JsonSerializerOptions PipeDataJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The characters JavaScript's encodeURI leaves un-escaped (alphanumerics are handled separately).
    private const string EncodeUriKeep = "-_.!~*'();,/?:@&=+$#";

    // The characters JavaScript's encodeURIComponent leaves un-escaped — used for the public-link filename
    // segment. NOTE this keeps "!~*'()" (which Uri.EscapeDataString wrongly escapes) so parentheses in a
    // filename stay literal, matching File Garden's own links.
    private const string EncodeUriComponentKeep = "-_.!~*'()";

    // (auth cookie, userId) cached per credentials id. One login at a time per id so a batch of N files
    // does ONE login, not N (same shape as Upstore's usid cache).
    private readonly ConcurrentDictionary<int, AuthState> _authByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getOverride;

    public FileGardenPipeline()
    {
    }

    /// <summary>Test ctor — stubs the login JSON POST, the raw upload, and (optionally) the pipe-list GET
    /// so the whole login → exists-check → upload orchestration runs without the network or a real file.</summary>
    internal FileGardenPipeline(
        Func<string, string?, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot>? getOverride = null)
    {
        _postJsonOverride = (url, json, headers) => Task.FromResult(postJsonOverride(url, json, headers));
        _uploadOverride = uploadOverride;
        _getOverride = getOverride is null ? null : (url, headers) => Task.FromResult(getOverride(url, headers));
    }

    public string Name => "FileGarden";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>File Garden caps a single upload at 100 MiB. Its own web app rejects a larger file
    /// client-side ("Currently, we don't support uploading files above 100 MiB."), and a bigger POST is
    /// 413'd at the Cloudflare edge before it even reaches the API. The wizard skips oversized files at
    /// queue time; <see cref="RunAsync"/> fails fast on any that slip through.</summary>
    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>File Garden requires an account (the upload endpoint 403s without a session).</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-flight: per-file 100 MiB cap. A bigger POST is 413'd at the Cloudflare edge (never
        // reaching the API), so reject it up front rather than waste the whole upload. ===
        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File Garden doesn't support files above {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: the account's auth cookie + userId (logs in + caches on first use) ===
        (AuthState? auth, string? loginError) = await EnsureAuthAsync(ctx.Handler, ctx.Credentials, ctx.Cancellation);
        if (auth is null)
        {
            yield return new AttemptFailed(loginError ?? "File Garden sign-in failed.", null);
            yield break;
        }

        // === Step 2: does a file with this name already exist? File Garden rejects a duplicate name in a
        // directory with a 422 — and that verdict only comes back AFTER the whole body is uploaded, so we
        // check first to avoid both the error and a wasted upload. ===
        (string? existingPath, bool nameConflict, string? conflictError) = await CheckExistingAsync(ctx, auth);
        if (nameConflict)
        {
            yield return new AttemptFailed(conflictError ?? "A file with this name already exists on File Garden.", null);
            yield break;
        }

        if (existingPath is not null)
        {
            // Same file (name + size) is already in the garden — the re-upload is a no-op; return its link.
            yield return new TransferStarted(ctx.FileSize);
            yield return new TransferCompleted(BuildPublicUrl(auth.UserId, existingPath));
            yield break;
        }

        // === Step 3: streamed raw POST to the user's pipe ===
        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via a channel (can't yield from inside the
        // event handler) — same pattern as the other streaming pipelines.
        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, auth);
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

        // Let a transport fault propagate to the shared retry layer: until the body is fully sent File
        // Garden has committed no file, so it arrives as a retryable UploadBodyTransferException and a
        // whole-pipeline retry re-uploads cleanly. A server verdict does NOT throw (the snapshot returns).
        HttpResponseSnapshot resp = await uploadTask;

        (string? id, string? path, bool authExpired, bool alreadyExists, string? uploadError) = ParseUploadResponse(resp);
        if (id is not null)
        {
            yield return new TransferCompleted(BuildPublicUrl(auth.UserId, path!));
            yield break;
        }

        if (alreadyExists)
        {
            // A race: the file appeared between the pre-check and this upload (or the pre-check couldn't
            // read the list). It exists now — return its link; a root upload stores it at path == name.
            yield return new TransferCompleted(BuildPublicUrl(auth.UserId, ctx.FileName));
            yield break;
        }

        // An auth failure means the cached session expired — drop the value WE used so the next attempt
        // re-logs-in (a concurrent attempt may already have installed a fresh one).
        if (authExpired)
        {
            ((ICollection<KeyValuePair<int, AuthState>>)_authByCredId)
                .Remove(new KeyValuePair<int, AuthState>(ctx.Credentials.Id, auth));
        }

        yield return new AttemptFailed(uploadError ?? "File Garden upload failed.", null);
    }

    /// <summary>Verifies a File Garden account by logging in — success is HTTP 201 with a userId + the
    /// <c>auth</c> cookie.</summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (AuthState? auth, string? error) = await LoginAsync(handler, username, password, ct);
        return auth is null
            ? new AccountCheckResult(false, AccountType.Free, error ?? "File Garden login failed.")
            : new AccountCheckResult(true, AccountType.Free, "Signed in (Free)", DerivedUsername: username);
    }

    /// <summary>Returns the cached (auth cookie, userId) for the account, logging in once (gated per
    /// credentials id) on a cache miss.</summary>
    private async Task<(AuthState? Auth, string? Error)> EnsureAuthAsync(HttpHandler handler, FileHosterLoginDto creds, CancellationToken ct)
    {
        int id = creds.Id;
        if (_authByCredId.TryGetValue(id, out AuthState? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_authByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (AuthState? auth, string? error) = await LoginAsync(handler, creds.Username, creds.Password, ct);
            if (auth is null)
            {
                return (null, error);
            }

            _authByCredId[id] = auth;
            return (auth, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>POSTs the login JSON and reads the userId (body <c>id</c>) plus the <c>auth</c> session
    /// cookie. A wrong/unverified account returns a JSON <c>{"error":"…"}</c> and no cookie.</summary>
    private async Task<(AuthState? Auth, string? Error)> LoginAsync(HttpHandler handler, string? email, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return (null, "File Garden account needs an email and password.");
        }

        // connection = "password " + base64(password); the browser sends the password base64-encoded.
        string connection = "password " + Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        string json = JsonSerializer.Serialize(new LoginBody { Connection = connection, Email = email });

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostJsonAsync(handler, TokenUrl, json, OriginHeaders(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "File Garden login request failed: " + ex.Message);
        }

        if (snap.StatusCode is >= 200 and < 300)
        {
            string? cookie = ExtractCookieValue(snap.SetCookies, AuthCookieName);
            string? userId = TryReadStringField(snap.Body, "id");
            if (!string.IsNullOrEmpty(cookie) && !string.IsNullOrEmpty(userId))
            {
                return (new AuthState(cookie, userId), null);
            }
        }

        string? apiError = TryReadStringField(snap.Body, "error");
        return (null, apiError is not null
            ? "File Garden login failed: " + apiError
            : $"File Garden login failed — check the email and password (HTTP {snap.StatusCode}).");
    }

    /// <summary>
    /// Lists the garden root (<c>GET /users/&lt;userId&gt;/pipe?parent=</c>, authenticated so the owner's
    /// private files are visible) and looks for a file with the same name. Returns the existing file's
    /// <c>path</c> when it's the same file (name + size match → the re-upload is a no-op), a
    /// <c>NameConflict</c> when a DIFFERENT file already owns the name (File Garden would 422 the upload),
    /// or nothing when the name is free. Any list/parse failure collapses to "not found" so the upload
    /// proceeds — the 422 safety net still covers a real conflict.
    /// </summary>
    private async Task<(string? ExistingPath, bool NameConflict, string? Error)> CheckExistingAsync(AttemptContext ctx, AuthState auth)
    {
        string url = ApiBase + "/users/" + auth.UserId + "/pipe?parent=";
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = AuthCookieName + "=" + auth.Cookie,
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx.Handler, url, headers, ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, false, null); // couldn't check — proceed to upload
        }

        if (snap.StatusCode is < 200 or >= 300)
        {
            return (null, false, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return (null, false, null);
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !string.Equals(Str(item, "name"), ctx.FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                // Same name. Same size (or size unknown) ⇒ same file, skip the upload. Different size ⇒ a
                // genuine clash File Garden won't let us resolve here.
                long? size = Num(item, "size");
                if (size is null || size == ctx.FileSize)
                {
                    return (Str(item, "path") ?? ctx.FileName, false, null);
                }

                return (null, true,
                    $"A different file named \"{ctx.FileName}\" ({ByteUnit.FromBytes(size.Value, ByteBase.Binary).ToFriendlyString()}) "
                    + "already exists on File Garden — rename or remove it there first.");
            }
        }
        catch (Exception)
        {
            return (null, false, null);
        }

        return (null, false, null); // name is free
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, AuthState auth)
    {
        string url = ApiBase + "/users/" + auth.UserId + "/pipe";

        // File metadata rides in the X-Data header as url-encoded JSON; the bytes are the body.
        string dataJson = JsonSerializer.Serialize(new PipeData { Parent = null, Name = ctx.FileName }, PipeDataJsonOptions);
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Cookie"] = AuthCookieName + "=" + auth.Cookie,
            ["X-Data"] = EncodeUri(dataJson),
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, url, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadFileBodyAsync(
            HttpMethod.Post,
            ctx.FilePath,
            url,
            contentType: "application/octet-stream",
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 201 with JSON <c>{"id":"…","path":"…"}</c>. <c>AuthExpired</c> is true on an auth
    /// error (HTTP 401/403) so the caller drops the stale cached session; <c>AlreadyExists</c> is true on
    /// the HTTP 422 "…already exists in the specified directory." verdict so the caller can treat it as a
    /// completed upload. Any other error surfaces with the API message.
    /// </summary>
    private static (string? Id, string? Path, bool AuthExpired, bool AlreadyExists, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is >= 200 and < 300)
        {
            string? id = TryReadStringField(response.Body, "id");
            string? path = TryReadStringField(response.Body, "path");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(path))
            {
                return (id, path, false, false, null);
            }
        }

        bool authExpired = response.StatusCode is 401 or 403;
        string? apiError = TryReadStringField(response.Body, "error");
        bool alreadyExists = response.StatusCode == 422
            && apiError?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true;
        return (null, null, authExpired, alreadyExists, $"File Garden upload failed (HTTP {response.StatusCode}): {apiError ?? Snippet(response.Body)}");
    }

    /// <summary>The public link: <c>https://file.garden/&lt;gardenId&gt;/&lt;path&gt;</c>. The garden id is
    /// derived from the userId (see <see cref="GardenId"/>); each path segment is encodeURIComponent-encoded
    /// (folder separators kept) so parentheses/apostrophes stay literal like File Garden's own links.</summary>
    private static string BuildPublicUrl(string userId, string path)
    {
        string encodedPath = string.Join('/', path.Split('/').Select(EncodeUriComponent));
        return PublicHost + "/" + GardenId(userId) + "/" + encodedPath;
    }

    /// <summary>File Garden's per-user "garden" id used in public links: URL-safe base64 of the 12 raw
    /// bytes of the 24-hex user id (e.g. <c>6a48f13c…c02</c> → <c>akjxPE7rAW98wmwC</c>). Falls back to the
    /// raw userId if it isn't valid hex (shouldn't happen — login returns a Mongo ObjectId).</summary>
    private static string GardenId(string userId)
    {
        try
        {
            byte[] bytes = Convert.FromHexString(userId);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        catch (Exception)
        {
            return userId;
        }
    }

    /// <summary>Percent-encodes like JavaScript's <c>encodeURIComponent</c> (keep-set
    /// <see cref="EncodeUriComponentKeep"/> + alphanumerics; UTF-8 percent bytes for everything else). Used
    /// for a public-link filename segment: unlike <see cref="Uri.EscapeDataString"/> it leaves
    /// <c>! ' ( ) *</c> literal, so parentheses match File Garden's own URLs.</summary>
    private static string EncodeUriComponent(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        StringBuilder sb = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || EncodeUriComponentKeep.Contains(c)))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> OriginHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
    };

    /// <summary>
    /// Percent-encodes exactly the way JavaScript's <c>encodeURI</c> does — the browser encodes the
    /// <c>X-Data</c> JSON with <c>encodeURI</c> (the capture leaves <c>:</c> and <c>,</c> literal).
    /// <see cref="Uri.EscapeDataString"/> would over-escape those (<c>encodeURIComponent</c> semantics),
    /// which a server decoding with <c>decodeURI</c> would fail to reverse. Non-ASCII is emitted as UTF-8
    /// percent bytes, matching the browser and round-tripping non-ASCII filenames.
    /// </summary>
    private static string EncodeUri(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        StringBuilder sb = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || EncodeUriKeep.Contains(c)))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
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

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    /// <summary>Reads the value of the named cookie from a response's raw <c>Set-Cookie</c> lines
    /// (<c>name=value; attr=…</c>). Null when absent or empty. The value stays url-encoded exactly as the
    /// server sent it — which is exactly how it must be echoed back in the request <c>Cookie</c> header.</summary>
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

    private Task<HttpResponseSnapshot> PostJsonAsync(HttpHandler handler, string url, string json, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        // File Garden's /token 400s ("That is not a valid email.") on a Content-Type with a charset
        // parameter — the browser sends a bare application/json, so we must too (jsonCharsetUtf8: false).
        => _postJsonOverride is not null ? _postJsonOverride(url, json, headers) : handler.SendJsonAsync(HttpMethod.Post, url, json, headers, ct, jsonCharsetUtf8: false);

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

    private sealed record AuthState(string Cookie, string UserId);

    private sealed class LoginBody
    {
        [JsonPropertyName("connection")] public string Connection { get; init; } = string.Empty;

        [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    }

    private sealed class PipeData
    {
        [JsonPropertyName("parent")] public string? Parent { get; init; }

        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    }
}
