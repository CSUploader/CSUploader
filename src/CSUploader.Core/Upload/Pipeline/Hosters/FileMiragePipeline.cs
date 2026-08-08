// <copyright file="FileMiragePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FileMirage (filemirage.com) — anonymous, <b>50 GiB</b>, chunked. Protocol read out of the site's
/// own Vue bundle, so no capture was needed:
/// <list type="number">
///   <item><b>Ask which node.</b> <c>GET /api/servers</c> →
///   <c>{"data":{"server":"https://storeN.filemirage.com","upload_id":"…"}}</c>. It answers keylessly;
///   the site only adds a bearer token when a signed-in user has one.</item>
///   <item><b>Send the chunks.</b> Multipart POST per chunk to <c>&lt;server&gt;/upload.php</c> with
///   <c>file</c>, <c>filename</c>, <c>upload_id</c>, <c>chunk_number</c> (0-based) and
///   <c>total_chunks</c>.</item>
///   <item><b>The last chunk answers with the link</b> — <c>{"data":{"url":"…"}}</c>.</item>
/// </list>
/// <para>
/// <b>The upload id is the client's to invent.</b> Its own uploader uses
/// <c>Date.now().toString(36)</c>; the lookup also returns an <c>upload_id</c> which the client
/// ignores. This uses random bytes instead of a timestamp: two files queued in the same millisecond
/// would otherwise share an id and be assembled into each other.
/// </para>
/// <para>
/// Its page declares <c>upload_chunk_size: 99</c> (MB) and <c>maxFileSize = 53687091200</c>, both used
/// here as given. <c>api_token</c> is empty for a guest and the server lookup needs none, so anonymous
/// is a first-class path rather than a fallback.
/// </para>
/// </summary>
public sealed class FileMiragePipeline : IFileHosterPipeline
{
    private const string Host = "https://filemirage.com";
    private const string ServersUrl = Host + "/api/servers";

    /// <summary>The page's own <c>maxFileSize</c> (50 GiB).</summary>
    private const long MaxFileSizeBytes = 53_687_091_200;

    /// <summary>The page's own <c>upload_chunk_size: 99</c>, in MB as its uploader multiplies it.</summary>
    private const int ChunkSizeBytes = 99 * 1024 * 1024;

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public FileMiragePipeline()
    {
    }

    /// <summary>Test ctor — stubs the node lookup and the per-chunk POST.</summary>
    internal FileMiragePipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, long, Task<HttpResponseSnapshot>> chunkOverride)
    {
        _getOverride = getOverride;
        _chunkOverride = chunkOverride;
    }

    public string Name => "FileMirage";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    /// <summary>Accounts exist, but nothing about them has been verified here — the anonymous path is
    /// what ships, and offering an account would mean a check this app can't make good on.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds FileMirage's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: which node ===
        (string? node, string? lookupError) = await ResolveNodeAsync(ctx);
        if (node is null)
        {
            yield return new AttemptFailed(lookupError!, null);
            yield break;
        }

        // === Step 2: the chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<(string? Url, string? Error)> uploadTask = SendChunksAsync(ctx, node);
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

        (string? url, string? error) = await uploadTask;
        if (url is null)
        {
            yield return new AttemptFailed(error ?? "FileMirage upload failed", null);
            yield break;
        }

        yield return new TransferCompleted(url);
    }

    private async Task<(string? Node, string? Error)> ResolveNodeAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot response;
        try
        {
            response = _getOverride is not null
                ? await _getOverride(ServersUrl)
                : await ctx.Handler.GetSnapshotAsync(ServersUrl, BrowserHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"FileMirage upload-node lookup failed: {ex.Message}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"FileMirage wouldn't name an upload node (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        string? server = ReadNode(response.Body);
        return server is null
            ? (null, $"FileMirage's node lookup carried no server: {Snippet(response.Body)}")
            : (server.TrimEnd('/'), null);
    }

    /// <summary>Reads <c>data.server</c> out of the lookup. Internal for testing.</summary>
    internal static string? ReadNode(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            return root.TryGetProperty("data", out JsonElement data)
                   && data.TryGetProperty("server", out JsonElement server)
                   && server.ValueKind == JsonValueKind.String
                   && server.GetString() is { Length: > 0 } url
                ? url
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(string? Url, string? Error)> SendChunksAsync(AttemptContext ctx, string node)
    {
        string endpoint = $"{node}/upload.php";

        // Its uploader keys the id on the clock, which collides for two files started in the same
        // millisecond — and a collision means two files assembled into one. Random bytes cost nothing.
        string uploadId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        long fileSize = ctx.FileSize;
        int totalChunks = fileSize <= ChunkSizeBytes ? 1 : (int)((fileSize + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long position = 0;

        for (int index = 0; index < totalChunks; index++)
        {
            long thisChunk = Math.Min(ChunkSizeBytes, fileSize - position);
            Dictionary<string, string> fields = new(StringComparer.Ordinal)
            {
                ["filename"] = ctx.FileName,
                ["upload_id"] = uploadId,
                ["chunk_number"] = index.ToString(CultureInfo.InvariantCulture),
                ["total_chunks"] = totalChunks.ToString(CultureInfo.InvariantCulture),
            };

            HttpResponseSnapshot response;
            if (_chunkOverride is not null)
            {
                response = await _chunkOverride(endpoint, fields, thisChunk);
            }
            else
            {
                file.Position = position;
                response = await ctx.Handler.PostChunkMultipartAsync(
                    endpoint,
                    new ChunkSliceStream(file, thisChunk),
                    thisChunk,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    fileFieldName: "file",
                    filePartName: ctx.FileName,
                    extraFields: fields,
                    headers: BrowserHeaders(),
                    getBytesPerSecond: ctx.SpeedLimitProvider,
                    cancellationToken: ctx.Cancellation);
            }

            (string? url, string? error) = ParseChunkResponse(response, index, totalChunks);
            if (error is not null)
            {
                return (null, error);
            }

            if (url is not null)
            {
                return (url, null);
            }

            position += thisChunk;
        }

        // Every chunk was accepted and none carried a link — the host changed its reply shape, and a
        // "successful" upload with no link is not one.
        return (null, "FileMirage accepted every chunk but returned no link.");
    }

    /// <summary>
    /// Reads one chunk reply: intermediate chunks answer without a URL, the last one carries
    /// <c>data.url</c>. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseChunkResponse(HttpResponseSnapshot response, int index, int total)
    {
        string where = $"chunk {(index + 1).ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}";

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"FileMirage rejected {where} (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"FileMirage's reply to {where} wasn't JSON: {Snippet(response.Body)}");
        }

        // The envelope carries its own success flag, and a false one can ride inside a 200.
        if (root.TryGetProperty("success", out JsonElement success)
            && success.ValueKind == JsonValueKind.False)
        {
            string? message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
            return (null, string.IsNullOrWhiteSpace(message)
                ? $"FileMirage refused {where}: {Snippet(response.Body)}"
                : $"FileMirage refused {where}: {message}");
        }

        string? url = root.TryGetProperty("data", out JsonElement data)
                      && data.TryGetProperty("url", out JsonElement u)
                      && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

        return (string.IsNullOrWhiteSpace(url) ? null : url, null);
    }

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Accept"] = "application/json, text/plain, */*",

        // Its own client sends an empty authorization for a guest rather than omitting the header.
        ["authorization"] = string.Empty,
    };

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

    /// <summary>No account support is offered — see <see cref="SupportsAccounts"/>.</summary>
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = username;
        _ = password;
        _ = apiKey;
        _ = handler;
        _ = proxy;
        _ = ct;
        return Task.FromResult(new AccountCheckResult(
            false,
            AccountType.Free,
            "FileMirage is used anonymously here — pick the built-in Anonymous option in the upload wizard."));
    }
}
