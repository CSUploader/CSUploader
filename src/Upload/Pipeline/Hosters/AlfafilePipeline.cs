// <copyright file="AlfafilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSUploader.Lib.Extensions;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Alfafile upload pipeline. The API contract is near-identical to Rapidgator's
/// (same envelope shape, same upload state machine 0=Uploading / 1=Processing /
/// 2=Done / 3=Fail), differing only in host, API version (v1 vs v2), and the
/// parameter name on folder/create (<c>folder_id</c> vs <c>parent_folder_id</c>).
/// Auth/login concurrency, post-upload polling, and hash-dedup behaviour all carry
/// over verbatim — see <see cref="RapidgatorPipeline"/> for design notes.
/// </summary>
public sealed class AlfafilePipeline : IFileHosterPipeline
{
    private const string ApiBase = "https://www.alfafile.net/api/v1";

    private readonly ConcurrentDictionary<int, AlfafileAuthState> _authByCredentialsId = new();

    // One login at a time per credentials id. Same rationale as RapidgatorPipeline:
    // 100 parallel uploads against the same account should share one login round-trip.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    // Folder-create cache keyed by (credentialsId, parent_folder_id, folder_name). Alfafile
    // returns HTTP 409 "Folder with the same name already exists" if the same name is
    // POSTed twice (unlike Rapidgator, which silently returns the existing folder), so
    // every file in a package would otherwise fail to set up its destination folder after
    // the first one succeeds. Caching the slug-style folder_id keeps subsequent files in
    // the same session reusing the original folder.
    private readonly ConcurrentDictionary<(int CredentialsId, string ParentFolderId, string FolderName), string> _foldersByName
        = new();

    private readonly Func<string, Task<string>>? _httpOverride;
    private readonly Func<string, string, Func<long?>?, Task>? _uploadOverride;

    /// <summary>Production ctor — uses <see cref="AttemptContext.Handler"/> for HTTP.</summary>
    public AlfafilePipeline()
    {
    }

    /// <summary>Test ctor — substitutes a synchronous responder for HTTP.</summary>
    internal AlfafilePipeline(Func<string, string> httpOverride)
    {
        _httpOverride = url => Task.FromResult(httpOverride(url));
    }

    /// <summary>Test ctor — substitutes both GET and multipart upload behaviour.</summary>
    internal AlfafilePipeline(Func<string, string> getOverride, Func<string, string, Func<long?>?, Task> uploadOverride)
    {
        _httpOverride = url => Task.FromResult(getOverride(url));
        _uploadOverride = uploadOverride;
    }

    /// <summary>Thrown internally when a post-auth API call returns HTTP 401 (token expired).</summary>
    private sealed class AuthExpiredException : Exception { }

    public string Name => "Alfafile";

    public bool RequiresHashingBeforeUpload => true;

    public bool RequiresHashingAfterUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        AlfafileAuthState auth;
        if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out AlfafileAuthState? cached))
        {
            auth = cached;
        }
        else
        {
            (AlfafileAuthState? gated, bool didLogin, string? error) = await EnsureAuthAsync(ctx, ct);

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
        bool authExpired = false;
        string? attemptFailure = null;
        bool attemptCancelled = false;
        Exception? attemptException = null;
        string? finalUrl = null;

        do
        {
            // === Folder ===
            string folderName = ResolveFolderName(ctx.FilePath);
            string? folderId;
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

            // === file/upload → upload_url + upload_id (or instant-finish on dedup) ===
            UploadUrlResult upload;
            try
            {
                upload = await GetUploadUrlAsync(ctx, auth, folderId);
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

            if (upload.CompletedFileUrl is not null)
            {
                finalUrl = upload.CompletedFileUrl;
                break;
            }

            if (upload.UploadUrl is not { } uploadUrl || upload.UploadId is not { } uploadId)
            {
                attemptFailure = "file/upload returned no upload_url";
                break;
            }

            // === Multipart upload bytes — bridge HttpHandler.UploadProgress to TransferProgress events ===
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

            await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return progressEv;
            }

            ctx.Handler.UploadProgress -= onProgress;

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

    private async Task<(AlfafileAuthState? Auth, bool DidLogin, string? Error)> EnsureAuthAsync(AttemptContext ctx, CancellationToken ct)
    {
        SemaphoreSlim gate = _loginGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out AlfafileAuthState? cached))
            {
                return (cached, false, null);
            }

            (AlfafileAuthState? newAuth, string? error) = await LoginAsync(ctx);
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

    private async Task<(AlfafileAuthState?, string?)> LoginAsync(AttemptContext ctx)
    {
        string url = $"{ApiBase}/user/login"
            + $"?login={Uri.EscapeDataString(ctx.Credentials.Username ?? string.Empty)}"
            + $"&password={Uri.EscapeDataString(ctx.Credentials.Password ?? string.Empty)}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env?.Status != 200 || env.Response is null)
        {
            return (null, FormatApiError("login failed", env?.Details, env?.Status, body));
        }

        // Alfafile has no per-account primary folder — uploads go to root (folder_id="0")
        // unless an explicit folder is created.
        return (new AlfafileAuthState(env.Response.Token, "0"), null);
    }

    /// <summary>
    /// Picks a sensible folder name for the uploaded file. Falls back to "uploads" when
    /// the file sits at a drive root (e.g. <c>D:\foo.iso</c>, where <c>DirectoryInfo</c>
    /// would return <c>"D:\"</c>) or when the parent name contains characters that aren't
    /// valid in a hoster folder name (colon, backslash, etc.).
    /// </summary>
    private static string ResolveFolderName(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            return "uploads";
        }

        string name = new DirectoryInfo(dir).Name;
        if (string.IsNullOrEmpty(name) || name.AsSpan().IndexOfAny(':', '\\', '/') >= 0)
        {
            return "uploads";
        }

        return name;
    }

    private async Task<(string? FolderId, string? Error)> CreateFolderAsync(AttemptContext ctx, AlfafileAuthState auth, string folderName)
    {
        // Reuse a folder we already touched earlier in this session — saves a round-trip
        // for every file in a package after the first.
        (int, string, string) cacheKey = (ctx.Credentials.Id, auth.PrimaryFolderId, folderName);
        if (_foldersByName.TryGetValue(cacheKey, out string? cachedId))
        {
            return (cachedId, null);
        }

        // Alfafile uses `folder_id` for the parent (Rapidgator uses `parent_folder_id`).
        string url = $"{ApiBase}/folder/create"
            + $"?name={Uri.EscapeDataString(folderName)}"
            + $"&folder_id={Uri.EscapeDataString(auth.PrimaryFolderId)}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (JsonHelpers.TryDeserializeObject(body, out FolderEnvelope? env) && env?.Status == 200 && env.Response?.Folder?.Id is { Length: > 0 } createdId)
        {
            _foldersByName[cacheKey] = createdId;
            return (createdId, null);
        }

        if (env?.Status == 401)
        {
            throw new AuthExpiredException();
        }

        // 409 → folder already exists from a previous session. Look it up on the server
        // via /folder/info on the parent — that response includes a `folders` array of
        // direct subfolder summaries. Match by name and cache.
        if (env?.Status == 409)
        {
            (string? existingId, string? lookupError) = await LookupChildFolderAsync(ctx, auth, auth.PrimaryFolderId, folderName);
            if (existingId is not null)
            {
                _foldersByName[cacheKey] = existingId;
                return (existingId, null);
            }

            return (null, FormatApiError("folder/create returned 409 and folder/info lookup failed: " + (lookupError ?? "subfolder not present"), env.Details, env.Status, body));
        }

        return (null, FormatApiError("folder/create failed", env?.Details, env?.Status, body));
    }

    /// <summary>
    /// Looks up a direct child folder by name under <paramref name="parentFolderId"/>
    /// via <c>/folder/info</c>. Returns the slug-style folder_id when found.
    /// </summary>
    private async Task<(string? FolderId, string? Error)> LookupChildFolderAsync(AttemptContext ctx, AlfafileAuthState auth, string parentFolderId, string folderName)
    {
        string url = $"{ApiBase}/folder/info"
            + $"?folder_id={Uri.EscapeDataString(parentFolderId)}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out FolderInfoEnvelope? env) || env?.Status != 200 || env.Response?.Folder is null)
        {
            if (env?.Status == 401) throw new AuthExpiredException();
            return (null, FormatApiError("folder/info failed", env?.Details, env?.Status, body));
        }

        FolderInfoSummary[] subfolders = env.Response.Folder.Subfolders ?? [];
        foreach (FolderInfoSummary sf in subfolders)
        {
            if (string.Equals(sf.Name, folderName, StringComparison.Ordinal) && sf.Id is { Length: > 0 })
            {
                return (sf.Id, null);
            }
        }

        return (null, $"no subfolder named '{folderName}' under parent {parentFolderId}");
    }

    private sealed record UploadUrlResult(string? UploadUrl, string? UploadId, string? CompletedFileUrl, string? Error);

    /// <summary>State value Alfafile returns when the file is already on their servers.</summary>
    private const int AlfafileUploadStateDone = 2;

    /// <summary>State value Alfafile returns when the server-side processing rejected the file.</summary>
    private const int AlfafileUploadStateFail = 3;

    private static readonly TimeSpan _uploadInfoPollTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan _uploadInfoPollMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _uploadInfoPollMaxDelay = TimeSpan.FromSeconds(5);

    private async Task<UploadUrlResult> GetUploadUrlAsync(AttemptContext ctx, AlfafileAuthState auth, string folderId)
    {
        string url = $"{ApiBase}/file/upload"
            + $"?folder_id={Uri.EscapeDataString(folderId)}"
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

        if (upload.State == AlfafileUploadStateDone && upload.File?.Url is { Length: > 0 } completedUrl)
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

    private async Task<(string?, string?)> GetUploadInfoAsync(AttemptContext ctx, AlfafileAuthState auth, string uploadId)
    {
        string url = $"{ApiBase}/file/upload_info?upload_id={uploadId}&token={auth.Token}";
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

            if (upload?.State == AlfafileUploadStateDone && upload.File?.Url is { Length: > 0 } fileUrl)
            {
                return (fileUrl, null);
            }

            if (upload?.State == AlfafileUploadStateFail)
            {
                string suffix = env.Details is { Length: > 0 } d ? $": {d}" : string.Empty;
                return (null, $"file/upload_info: server rejected the upload (state 3){suffix} (HTTP {env.Status})");
            }

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

            TimeSpan next = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            delay = next > _uploadInfoPollMaxDelay ? _uploadInfoPollMaxDelay : next;
        }
    }

    private Task<string> GetAsync(AttemptContext ctx, string url)
        => _httpOverride is not null ? _httpOverride(url) : ctx.Handler.GetStringAsync(url, ctx.Cancellation);

    private static string FormatApiError(string fallback, string? details, int? status, string? rawBody = null)
    {
        if (status is int s)
        {
            string head = details is { Length: > 0 } ? details : fallback;
            return $"{head} (HTTP {s})";
        }

        if (details is { Length: > 0 })
        {
            return details;
        }

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
        // Alfafile folder IDs are short slugs (e.g. "GCtX") — not integers, so they
        // stay as strings end-to-end. The implicit root folder is "0".
        [JsonPropertyName("folder_id")] public string? Id { get; set; }
    }

    private sealed class FolderInfoEnvelope
    {
        [JsonPropertyName("response")] public FolderInfoResponseBody? Response { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class FolderInfoResponseBody
    {
        [JsonPropertyName("folder")] public FolderInfoBody? Folder { get; set; }
    }

    private sealed class FolderInfoBody
    {
        [JsonPropertyName("folder_id")] public string? Id { get; set; }

        /// <summary>Direct child folders of the queried folder.</summary>
        [JsonPropertyName("folders")] public FolderInfoSummary[]? Subfolders { get; set; }
    }

    private sealed class FolderInfoSummary
    {
        [JsonPropertyName("folder_id")] public string? Id { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }
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

        [JsonPropertyName("state")] public int State { get; set; }
    }

    private sealed class UploadInfoFile
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
