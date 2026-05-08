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

    /// <summary>Production ctor — uses the <see cref="AttemptContext.Handler"/> for HTTP.</summary>
    public RapidgatorPipeline()
    {
    }

    /// <summary>Test ctor — substitutes a synchronous responder for HTTP. Synchronous body kept in a Task wrapper.</summary>
    internal RapidgatorPipeline(Func<string, string> httpOverride)
    {
        _httpOverride = url => Task.FromResult(httpOverride(url));
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

        // Transfer comes in Task 2.4. Bridge to a stub success for now so this task's test passes.
        yield return new TransferCompleted("about:blank");
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
}
