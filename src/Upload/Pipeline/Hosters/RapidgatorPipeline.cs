// <copyright file="RapidgatorPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSUploader.Lib.Extensions;

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

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        RapidgatorAuthState? auth;
        if (!_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out auth))
        {
            (RapidgatorAuthState? gated, bool didLogin, string? error) = await EnsureAuthAsync(ctx, ct);
            auth = gated;

            if (didLogin)
            {
                yield return new AuthStarted();
            }

            if (auth is null)
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
                (folderId, folderError) = await CreateFolderAsync(ctx, auth!, folderName);
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
                upload = await GetUploadUrlAsync(ctx, auth!, folderId.Value);
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

            string uploadUrl = upload.UploadUrl!;
            string uploadId = upload.UploadId!;

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
                (fileUrl, infoError) = await GetUploadInfoAsync(ctx, auth!, uploadId);
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
        string url = $"https://www.rapidgator.net/api/v2/user/login"
            + $"?login={Uri.EscapeDataString(ctx.Credentials.Username ?? string.Empty)}"
            + $"&password={Uri.EscapeDataString(ctx.Credentials.Password ?? string.Empty)}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env?.Status != 200 || env.Response is null)
        {
            return (null, FormatApiError("login failed", env?.Details, env?.Status, body));
        }

        return (new RapidgatorAuthState(env.Response.Token, env.Response.User?.FolderId ?? 0), null);
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
        if (upload.State == RapidgatorUploadStateDone && !string.IsNullOrEmpty(upload.File?.Url))
        {
            return new UploadUrlResult(null, null, upload.File!.Url, null);
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
        string url = $"https://www.rapidgator.net/api/v2/file/upload_info?upload_id={uploadId}&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out UploadInfoEnvelope? env) || env?.Status != 200 || env.Response?.Upload?.File?.Url is null)
        {
            if (env?.Status == 401) throw new AuthExpiredException();
            return (null, FormatApiError("file/upload_info failed", env?.Details, env?.Status, body));
        }

        return (env.Response.Upload.File.Url, null);
    }

    private Task<string> GetAsync(AttemptContext ctx, string url)
        => _httpOverride is not null ? _httpOverride(url) : ctx.Handler.GetStringAsync(url, ctx.Cancellation);

    private static string FormatApiError(string fallback, string? details, int? status, string? rawBody = null)
    {
        // Best case: server returned a structured envelope. Prefer its details over our
        // generic fallback, and append the HTTP status when we have one.
        if (status is int s)
        {
            string head = string.IsNullOrEmpty(details) ? fallback : details!;
            return $"{head} (HTTP {s})";
        }

        if (!string.IsNullOrEmpty(details))
        {
            return details!;
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
    }

    private sealed class UploadInfoFile
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
