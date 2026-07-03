// <copyright file="CatboxPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// catbox.moe upload pipeline — anonymous, no account. A single multipart POST to the fixed
/// <c>https://catbox.moe/user/api.php</c> endpoint (no homepage scrape, no login): fields
/// <c>reqtype=fileupload</c> + <c>fileToUpload=&lt;bytes&gt;</c>. The response body is the plain-text
/// share URL (<c>https://files.catbox.moe/&lt;code&gt;.&lt;ext&gt;</c>); a failure comes back as a
/// non-URL error string. No hashing. Verified against a live anonymous capture 2026-07-03.
/// </summary>
public sealed class CatboxPipeline : IFileHosterPipeline
{
    private const string ApiUrl = "https://catbox.moe/user/api.php";
    private const string Host = "https://catbox.moe";
    private const string FilesPrefix = "https://files.catbox.moe/";

    /// <summary>Anonymous per-file cap — catbox.moe's documented 200 MB limit. Rejected client-side so
    /// an oversized file never wastes an upload; the server enforces its own limit regardless.</summary>
    private const long MaxAnonymousFileSizeBytes = 200L * 1024 * 1024;

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public CatboxPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload so the orchestration (event sequence, share URL,
    /// error handling) runs without the network.</summary>
    internal CatboxPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "Catbox";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxAnonymousFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>catbox.moe needs no account — the wizard offers it as a built-in "Anonymous" option.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxAnonymousFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds catbox.moe's {ByteUnit.FromBytes(MaxAnonymousFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't yield
        // from inside the event handler) — same pattern as the other pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx);
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

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): a connect-phase
        // failure or mid-send abort arrives as a safe-to-retry UploadBodyTransferException, and re-running
        // never double-creates because the body never finished. A SERVER VERDICT does not throw
        // (UploadMultipartAsync returns the snapshot), so it flows through ParseUploadResponse below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>catbox.moe has no account sign-in in this app — uploads use the built-in Anonymous option.</summary>
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
            "catbox.moe has no account sign-in — upload with the built-in Anonymous option in the wizard."));
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["reqtype"] = "fileupload",
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
            ["Accept"] = "application/json",
            // The site's Dropzone marks the upload as an XHR.
            ["X-Requested-With"] = "XMLHttpRequest",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, ApiUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            ApiUrl,
            fileFieldName: "fileToUpload",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 200 with a body that is the plain <c>https://files.catbox.moe/…</c> URL. A
    /// non-2xx, or a 2xx whose body isn't a catbox URL (catbox returns a plain error string like
    /// "Something went wrong.") surfaces as a failure with the body snippet.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"catbox.moe upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        string body = response.Body.Trim();
        if (body.StartsWith(FilesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (body, null);
        }

        // catbox echoes a plain-text reason on failure (e.g. a too-big file or a banned type).
        return (null, string.IsNullOrEmpty(body)
            ? $"catbox.moe upload returned an empty response (HTTP {response.StatusCode})."
            : $"catbox.moe upload was rejected: {Snippet(body)}");
    }

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
