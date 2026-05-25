// <copyright file="RapidgatorPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSUploader.Lib.Extensions;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

public sealed class RapidgatorPipeline : IFileHosterPipeline
{
    private readonly ConcurrentDictionary<int, RapidgatorAuthState> _authByCredentialsId = new();

    // One login at a time per credentials id. Without this, kicking off N parallel
    // uploads against the same account triggers N concurrent login round-trips, which
    // Rapidgator rate-limits with "Frequent logins. Please wait 20 seconds…". The
    // gate's leader does the login and writes the cache; followers double-check and
    // reuse it without extra API calls.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, Task<string>>? _httpOverride;
    private readonly Func<string, string, Func<long?>?, Task>? _uploadOverride;

    /// <summary>Production ctor — uses the <see cref="AttemptContext.Handler"/> for HTTP.</summary>
    public RapidgatorPipeline()
    {
    }

    /// <summary>Test ctor — substitutes a synchronous responder for HTTP. Synchronous body kept in a Task wrapper.</summary>
    internal RapidgatorPipeline(Func<string, string> httpOverride)
    {
        _httpOverride = url => Task.FromResult(httpOverride(url));
    }

    /// <summary>Test ctor — substitutes both GET and multipart upload behaviour.</summary>
    internal RapidgatorPipeline(Func<string, string> getOverride, Func<string, string, Func<long?>?, Task> uploadOverride)
    {
        _httpOverride = url => Task.FromResult(getOverride(url));
        _uploadOverride = uploadOverride;
    }

    /// <summary>Test ctor — async GET override so concurrency tests can introduce delays.</summary>
    internal RapidgatorPipeline(Func<string, Task<string>> asyncGetOverride)
    {
        _httpOverride = asyncGetOverride;
    }

    /// <summary>Thrown internally when a post-auth API call returns HTTP 401 (token expired).</summary>
    private sealed class AuthExpiredException : Exception { }

    public string Name => "Rapidgator";

    public bool RequiresHashingBeforeUpload => true;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        RapidgatorAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out RapidgatorAuthState? cached))
        {
            auth = cached;
        }
        else
        {
            (RapidgatorAuthState? gated, bool didLogin, string? error) = await EnsureAuthAsync(ctx, ct);

            if (didLogin)
            {
                yield return new AuthStarted();
            }

            if (gated is null)
            {
                if (didLogin)
                {
                    yield return new AuthFailed(error ?? "login returned no token");
                }
                yield return new AttemptFailed(error ?? "login failed", null);
                yield break;
            }

            if (didLogin)
            {
                yield return new AuthSucceeded();
            }

            auth = gated;
        }

        // === Post-auth flow (folder → transfer → upload_info) ===
        // yield return is illegal in catch, so we collect outcome flags and yield after the try blocks.
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        // do/while(false) lets us `break` out of a linear flow on early failure without goto.
        do
        {
            // === Folder ===
            string folderName = ResolveFolderName(ctx.FilePath);
            int? folderId;
            string? folderError;
            try
            {
                (folderId, folderError) = await CreateFolderAsync(ctx, auth, folderName);
            }
            catch (AuthExpiredException)
            {
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                authExpired = true;
                break;
            }

            if (folderId is null)
            {
                attemptFailure = folderError ?? "folder/create failed";
                break;
            }

            yield return new TransferStarted(ctx.FileSize);

            // === File upload request → upload_url + upload_id (or instant-finish) ===
            UploadUrlResult upload;
            try
            {
                upload = await GetUploadUrlAsync(ctx, auth, folderId.Value);
            }
            catch (AuthExpiredException)
            {
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                authExpired = true;
                break;
            }

            if (upload.Error is not null)
            {
                attemptFailure = upload.Error;
                break;
            }

            // Hash-dedup hit — server already had this file. Skip bytes upload and the
            // upload_info round-trip; the public URL is right here.
            if (upload.CompletedFileUrl is not null)
            {
                finalUrl = upload.CompletedFileUrl;
                break;
            }

            // GetUploadUrlAsync guarantees both fields are populated when neither Error nor
            // CompletedFileUrl is set, but the compiler can't see that — bail defensively.
            if (upload.UploadUrl is not { } uploadUrl || upload.UploadId is not { } uploadId)
            {
                attemptFailure = "file/upload returned no upload_url";
                break;
            }

            // === Multipart upload bytes — bridge HttpHandler.UploadProgress to TransferProgress events ===
            // UploadBytesAsync runs concurrently; progress callbacks write into an unbounded
            // channel that this iterator drains. The upload task's completion (including its
            // exceptions) is surfaced after the channel is fully drained.
            Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
            EventHandler<Lib.OperationProgressEventArgs> onProgress = (_, e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task uploadTask = UploadBytesAsync(ctx, uploadUrl);
            _ = uploadTask.ContinueWith(
                _ => progressChannel.Writer.Complete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Do not pass the cancellation token here: when the token fires, UploadBytesAsync
            // will throw, the ContinueWith will complete the writer, and ReadAllAsync will
            // drain naturally. Passing the token would cause ReadAllAsync itself to throw
            // OperationCanceledException before the channel is fully drained.
            await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return progressEv;
            }

            ctx.Handler.UploadProgress -= onProgress;

            // Surface the upload task's outcome after the channel is fully drained.
            try
            {
                await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                attemptCancelled = true;
                break;
            }
            catch (Exception ex)
            {
                attemptException = ex;
                break;
            }

            // === Upload info → public URL ===
            string? fileUrl;
            string? infoError;
            try
            {
                (fileUrl, infoError) = await GetUploadInfoAsync(ctx, auth, uploadId);
            }
            catch (AuthExpiredException)
            {
                _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
                authExpired = true;
                break;
            }

            if (fileUrl is null)
            {
                attemptFailure = infoError ?? "file/upload_info failed";
                break;
            }

            finalUrl = fileUrl;
        }
        while (false);

        // Yield terminal events based on outcome flags (yield illegal in catch).
        if (authExpired)
        {
            yield return new AuthFailed("token expired");
            yield return new AttemptFailed("token expired — retry will re-authenticate", null);
            yield break;
        }

        if (attemptCancelled)
        {
            yield return new AttemptCancelled();
            yield break;
        }

        if (attemptException is not null)
        {
            yield return new AttemptFailed(attemptException.Message, attemptException);
            yield break;
        }

        if (attemptFailure is not null)
        {
            yield return new AttemptFailed(attemptFailure, null);
            yield break;
        }

        if (finalUrl is not null)
        {
            yield return new TransferCompleted(finalUrl);
        }
    }

    /// <summary>
    /// Acquires the per-credentials gate, double-checks the cache, and either logs in
    /// (for the first caller) or returns the cached state (for everyone after).
    /// </summary>
    /// <returns>
    /// (auth, didLogin, error). <c>didLogin</c> is true only for the leader that
    /// performed the actual round-trip; followers see the cache and report didLogin=false
    /// so RunAsync can suppress the AuthStarted/AuthSucceeded events that would otherwise
    /// fire redundantly per file.
    /// </returns>
    private async Task<(RapidgatorAuthState? Auth, bool DidLogin, string? Error)> EnsureAuthAsync(AttemptContext ctx, CancellationToken ct)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out RapidgatorAuthState? cached))
            {
                return (cached, false, null);
            }

            (RapidgatorAuthState? newAuth, string? error) = await LoginAsync(ctx);
            if (newAuth is null)
            {
                return (null, true, error);
            }

            _authByCredentialsId[ctx.Credentials.Id] = newAuth;
            return (newAuth, true, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(RapidgatorAuthState?, string?)> LoginAsync(AttemptContext ctx)
    {
        string url = BuildLoginUrl(ctx.Credentials.Username, ctx.Credentials.Password);
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env?.Status != 200 || env.Response is null)
        {
            return (null, FormatApiError("login failed", env?.Details, env?.Status, body));
        }

        return (new RapidgatorAuthState(env.Response.Token, env.Response.User?.FolderId ?? 0), null);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy; // Rapidgator's REST login doesn't need the proxy choice separately — the handler already routes through it.
        _ = apiKey; // Rapidgator doesn't support API keys.
        string url = BuildLoginUrl(username, password);

        string body;
        try
        {
            body = _httpOverride is not null ? await _httpOverride(url) : await handler.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, ex.Message);
        }

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env is null)
        {
            return new AccountCheckResult(false, AccountType.Free, "login: unexpected response");
        }

        if (env.Status != 200 || env.Response is null)
        {
            return new AccountCheckResult(false, AccountType.Free, env.Details ?? $"login failed (HTTP {env.Status})");
        }

        return BuildAccountCheckResult(env.Response.User?.IsPremium == true, env.Response.User?.PremiumEndTime);
    }

    private static string BuildLoginUrl(string? username, string? password)
        => $"https://www.rapidgator.net/api/v2/user/login"
            + $"?login={Uri.EscapeDataString(username ?? string.Empty)}"
            + $"&password={Uri.EscapeDataString(password ?? string.Empty)}";

    private static AccountCheckResult BuildAccountCheckResult(bool isPremium, long? premiumEndTimeUnix)
    {
        DateTime? expiry = premiumEndTimeUnix is { } ts and > 0
            ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
            : null;

        AccountType type = isPremium ? AccountType.Premium : AccountType.Free;
        string message = isPremium
            ? (expiry is { } e ? string.Format(CultureInfo.InvariantCulture, "Premium until {0:yyyy-MM-dd}", e) : "Premium")
            : "Free";

        return new AccountCheckResult(true, type, message, expiry);
    }

    private static string ResolveFolderName(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        return string.IsNullOrEmpty(dir) ? "uploads" : new DirectoryInfo(dir).Name;
    }

    private async Task<(int? FolderId, string? Error)> CreateFolderAsync(AttemptContext ctx, RapidgatorAuthState auth, string folderName)
    {
        string url = $"https://www.rapidgator.net/api/v2/folder/create"
            + $"?name={Uri.EscapeDataString(folderName)}"
            + $"&parent_folder_id={auth.PrimaryFolderId}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out FolderEnvelope? env) || env?.Status != 200 || env.Response?.Folder is null)
        {
            if (env?.Status == 401) throw new AuthExpiredException();
            return (null, FormatApiError("folder/create failed", env?.Details, env?.Status, body));
        }

        return (env.Response.Folder.Id, null);
    }

    /// <summary>
    /// Result of /api/v2/file/upload. Three mutually-exclusive shapes:
    ///   - <see cref="Error"/> set: API rejected the request (size cap, auth, etc.).
    ///   - <see cref="CompletedFileUrl"/> set: hash dedup hit — Rapidgator already had this
    ///     file, so they instantly assigned it to our account and returned the public URL
    ///     without us uploading any bytes. Skip the multipart + upload_info steps.
    ///   - <see cref="UploadUrl"/> + <see cref="UploadId"/> set: normal flow, POST bytes.
    /// </summary>
    private sealed record UploadUrlResult(string? UploadUrl, string? UploadId, string? CompletedFileUrl, string? Error);

    /// <summary>State value Rapidgator returns when the file is already on their servers.</summary>
    private const int RapidgatorUploadStateDone = 2;

    /// <summary>State value Rapidgator returns when the server-side post-upload processing
    /// determined the file is bad/rejected. Terminal failure — stop polling upload_info.</summary>
    private const int RapidgatorUploadStateFail = 3;

    /// <summary>Hard cap on how long we wait for Rapidgator to finish processing an upload
    /// after the bytes are transferred. Most files finish within seconds; a slow day can
    /// stretch to a minute or two for very large files.</summary>
    private static readonly TimeSpan _uploadInfoPollTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Minimum/maximum gap between consecutive upload_info polls. Backs off
    /// exponentially between these bounds.</summary>
    private static readonly TimeSpan _uploadInfoPollMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _uploadInfoPollMaxDelay = TimeSpan.FromSeconds(5);

    private async Task<UploadUrlResult> GetUploadUrlAsync(AttemptContext ctx, RapidgatorAuthState auth, int folderId)
    {
        string url = $"https://www.rapidgator.net/api/v2/file/upload"
            + $"?folder_id={folderId}"
            + $"&name={Uri.EscapeDataString(ctx.FileName)}"
            + $"&hash={ctx.FileHash}"
            + $"&size={ctx.FileSize}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out UploadUrlEnvelope? env) || env?.Status != 200 || env.Response?.Upload is null)
        {
            if (env?.Status == 401) throw new AuthExpiredException();
            return new UploadUrlResult(null, null, null, FormatApiError("file/upload failed", env?.Details, env?.Status, body));
        }

        UploadUrl upload = env.Response.Upload;

        // Hash-dedup short-circuit. The server returns `url: null`, `state: 2 ("Done")`,
        // and a populated `file.url` we can use directly.
        if (upload.State == RapidgatorUploadStateDone && upload.File?.Url is { Length: > 0 } completedUrl)
        {
            return new UploadUrlResult(null, null, completedUrl, null);
        }

        if (string.IsNullOrEmpty(upload.Url) || string.IsNullOrEmpty(upload.UploadId))
        {
            return new UploadUrlResult(null, null, null, FormatApiError("file/upload failed", env?.Details, env?.Status, body));
        }

        return new UploadUrlResult(upload.Url, upload.UploadId, null, null);
    }

    private Task UploadBytesAsync(AttemptContext ctx, string uploadUrl)
        => _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, uploadUrl, ctx.SpeedLimitProvider)
            : ctx.Handler.UploadFileAsync(ctx.FilePath, uploadUrl, ctx.SpeedLimitProvider, ctx.Cancellation);

    private async Task<(string?, string?)> GetUploadInfoAsync(AttemptContext ctx, RapidgatorAuthState auth, string uploadId)
    {
        // After the bytes upload, Rapidgator post-processes the file on their side. Until
        // that finishes the response is a HTTP 200 with `upload.state` in {0,1} and the
        // public `file.url` still null. Poll with backoff until state hits 2 (Done) or
        // 3 (Fail), or we run out the timeout budget.
        string url = $"https://www.rapidgator.net/api/v2/file/upload_info?upload_id={uploadId}&token={auth.Token}";
        DateTime deadline = DateTime.UtcNow + _uploadInfoPollTimeout;
        TimeSpan delay = _uploadInfoPollMinDelay;

        while (true)
        {
            string body = await GetAsync(ctx, url);

            if (!JsonHelpers.TryDeserializeObject(body, out UploadInfoEnvelope? env))
            {
                return (null, FormatApiError("file/upload_info: response was not JSON", null, null, body));
            }

            if (env?.Status == 401)
            {
                throw new AuthExpiredException();
            }

            if (env?.Status != 200)
            {
                return (null, FormatApiError("file/upload_info failed", env?.Details, env?.Status, body));
            }

            UploadInfoUpload? upload = env.Response?.Upload;

            // Terminal success: server is done processing and gave us the public URL.
            if (upload?.State == RapidgatorUploadStateDone && upload.File?.Url is { Length: > 0 } fileUrl)
            {
                return (fileUrl, null);
            }

            // Terminal failure: server-side processing rejected the file. We build the
            // message inline (instead of letting FormatApiError pick `details` over our
            // fallback) so the "state 3" diagnostic is always visible — knowing this came
            // from the post-upload state machine rather than a transport error matters when
            // triaging.
            if (upload?.State == RapidgatorUploadStateFail)
            {
                string suffix = env.Details is { Length: > 0 } d ? $": {d}" : string.Empty;
                return (null, $"file/upload_info: server rejected the upload (state 3){suffix} (HTTP {env.Status})");
            }

            // Out of budget — give up. Surfacing the last state we saw helps the user
            // tell "stuck processing" from "missing field" from "unexpected shape".
            if (DateTime.UtcNow >= deadline)
            {
                string detail = upload is null
                    ? "no upload payload"
                    : $"state={upload.State}, url={(upload.File?.Url is { Length: > 0 } u ? u : "(null)")}";
                return (null, FormatApiError($"file/upload_info: timed out waiting for processing ({detail})", env.Details, env.Status, body));
            }

            try
            {
                await Task.Delay(delay, ctx.Cancellation);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            // Exponential backoff, capped at MaxDelay.
            TimeSpan next = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            delay = next > _uploadInfoPollMaxDelay ? _uploadInfoPollMaxDelay : next;
        }
    }

    private Task<string> GetAsync(AttemptContext ctx, string url)
        => _httpOverride is not null ? _httpOverride(url) : ctx.Handler.GetStringAsync(url, ctx.Cancellation);

    private static string FormatApiError(string fallback, string? details, int? status, string? rawBody = null)
    {
        // Best case: server returned a structured envelope. Prefer its details over our
        // generic fallback, and append the HTTP status when we have one.
        if (status is int s)
        {
            string head = details is { Length: > 0 } ? details : fallback;
            return $"{head} (HTTP {s})";
        }

        if (details is { Length: > 0 })
        {
            return details;
        }

        // Worst case: deserialization failed (env was null) — body wasn't shaped like our
        // envelope at all. Tail a snippet of the raw body so the user can see what came
        // back (HTML error page, plain-text reject, etc.) instead of just "X failed".
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            string snippet = rawBody.Trim()
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            const int Max = 200;
            if (snippet.Length > Max)
            {
                snippet = snippet[..Max] + "…";
            }

            return $"{fallback}: {snippet}";
        }

        return fallback;
    }

    private sealed class LoginEnvelope
    {
        [JsonPropertyName("response")] public LoginResponse? Response { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class LoginResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;

        [JsonPropertyName("user")] public LoginUser? User { get; set; }
    }

    private sealed class LoginUser
    {
        [JsonPropertyName("folder_id")] public int FolderId { get; set; }

        [JsonPropertyName("email")] public string? Email { get; set; }

        [JsonPropertyName("is_premium")] public bool IsPremium { get; set; }

        // Rapidgator reports the premium expiry as either a unix timestamp (number) or
        // null for free accounts. AllowReadingFromString (configured on the shared
        // JsonHelpers options) also accepts string-encoded numbers.
        [JsonPropertyName("premium_end_time")] public long? PremiumEndTime { get; set; }
    }

    private sealed class FolderEnvelope
    {
        [JsonPropertyName("response")] public FolderResponseBody? Response { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class FolderResponseBody
    {
        [JsonPropertyName("folder")] public FolderDetail? Folder { get; set; }
    }

    private sealed class FolderDetail
    {
        [JsonPropertyName("folder_id")] public int Id { get; set; }
    }

    private sealed class UploadUrlEnvelope
    {
        [JsonPropertyName("response")] public UploadUrlResponse? Response { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class UploadUrlResponse
    {
        [JsonPropertyName("upload")] public UploadUrl? Upload { get; set; }
    }

    private sealed class UploadUrl
    {
        [JsonPropertyName("upload_id")] public string UploadId { get; set; } = string.Empty;

        // Null on the hash-dedup path — Rapidgator skips the bytes upload and returns the
        // public URL directly inside `file`. Default empty kept for the normal flow.
        [JsonPropertyName("url")] public string? Url { get; set; } = string.Empty;

        [JsonPropertyName("state")] public int State { get; set; }

        [JsonPropertyName("file")] public UploadUrlFile? File { get; set; }
    }

    private sealed class UploadUrlFile
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class UploadInfoEnvelope
    {
        [JsonPropertyName("response")] public UploadInfoResponse? Response { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class UploadInfoResponse
    {
        [JsonPropertyName("upload")] public UploadInfoUpload? Upload { get; set; }
    }

    private sealed class UploadInfoUpload
    {
        [JsonPropertyName("file")] public UploadInfoFile? File { get; set; }

        /// <summary>Upload pipeline state: 0=Uploading, 1=Processing, 2=Done, 3=Fail.</summary>
        [JsonPropertyName("state")] public int State { get; set; }
    }

    private sealed class UploadInfoFile
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
