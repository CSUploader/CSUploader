// <copyright file="RapidgatorPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CSUploader.Lib.Extensions;

namespace CSUploader.Upload.Pipeline.Hosters;

public sealed class RapidgatorPipeline : IFileHosterPipeline
{
    private readonly ConcurrentDictionary<int, RapidgatorAuthState> _authByCredentialsId = new();
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

    public string Name => "Rapidgator";

    public bool RequiresHashingBeforeUpload => true;

    public bool RequiresHashingAfterUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        if (!_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out RapidgatorAuthState? auth))
        {
            yield return new AuthStarted();

            (RapidgatorAuthState? newAuth, string? error) = await LoginAsync(ctx);
            if (newAuth is null)
            {
                yield return new AuthFailed(error ?? "login returned no token");
                yield return new AttemptFailed(error ?? "login failed", null);
                yield break;
            }

            _authByCredentialsId[ctx.Credentials.Id] = newAuth;
            auth = newAuth;
            yield return new AuthSucceeded();
        }

        // === Folder ===
        string folderName = ResolveFolderName(ctx.FilePath);
        (int? folderId, string? folderError) = await CreateFolderAsync(ctx, auth!, folderName);
        if (folderId is null)
        {
            yield return new AttemptFailed(folderError ?? "folder/create failed", null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // === File upload request → upload_url + upload_id ===
        (string? uploadUrl, string? uploadId, string? upError) = await GetUploadUrlAsync(ctx, auth!, folderId.Value);
        if (uploadUrl is null || uploadId is null)
        {
            yield return new AttemptFailed(upError ?? "file/upload failed", null);
            yield break;
        }

        // === Multipart upload bytes ===
        Exception? uploadException = null;
        try
        {
            await UploadBytesAsync(ctx, uploadUrl);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            uploadException = null; // handled below via flag
        }
        catch (Exception ex)
        {
            uploadException = ex;
        }

        if (ctx.Cancellation.IsCancellationRequested && uploadException is null)
        {
            yield return new AttemptCancelled();
            yield break;
        }

        if (uploadException is not null)
        {
            yield return new AttemptFailed(uploadException.Message, uploadException);
            yield break;
        }

        // === Upload info → public URL ===
        (string? fileUrl, string? infoError) = await GetUploadInfoAsync(ctx, auth!, uploadId);
        if (fileUrl is null)
        {
            yield return new AttemptFailed(infoError ?? "file/upload_info failed", null);
            yield break;
        }

        yield return new TransferCompleted(fileUrl);
    }

    private async Task<(RapidgatorAuthState?, string?)> LoginAsync(AttemptContext ctx)
    {
        string url = $"https://www.rapidgator.net/api/v2/user/login"
            + $"?login={Uri.EscapeDataString(ctx.Credentials.Username ?? string.Empty)}"
            + $"&password={Uri.EscapeDataString(ctx.Credentials.Password ?? string.Empty)}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env?.Status != 200 || env.Response is null)
        {
            return (null, env?.Details ?? "login failed");
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
            return (null, env?.Details ?? "folder/create failed");
        }

        return (env.Response.Folder.Id, null);
    }

    private async Task<(string?, string?, string?)> GetUploadUrlAsync(AttemptContext ctx, RapidgatorAuthState auth, int folderId)
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
            return (null, null, env?.Details ?? "file/upload failed");
        }

        return (env.Response.Upload.Url, env.Response.Upload.UploadId, null);
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
            return (null, env?.Details ?? "file/upload_info failed");
        }

        return (env.Response.Upload.File.Url, null);
    }

    private Task<string> GetAsync(AttemptContext ctx, string url)
        => _httpOverride is not null ? _httpOverride(url) : ctx.Handler.GetStringAsync(url, ctx.Cancellation);

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

        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
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
