// <copyright file="LitterboxPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Litterbox — catbox.moe's temporary sibling, run by the same people on the same API shape: one
/// multipart POST to <c>/resources/internals/api.php</c> with <c>reqtype=fileupload</c>,
/// <c>fileToUpload=&lt;bytes&gt;</c> and a <c>time</c> field, answering with the plain share URL
/// (<c>https://litter.catbox.moe/&lt;code&gt;.&lt;ext&gt;</c>). Verified with real bytes 2026-08-03.
/// <para>
/// The trade against <see cref="CatboxPipeline"/> is size for permanence: catbox keeps files forever
/// but caps them at 200 MB, Litterbox takes <b>1 GB</b> and deletes them. <b>⚠ 72 hours is the longest
/// retention it offers</b> (its options are 1h/12h/24h/72h) and this ships the longest one, but a link
/// posted on Friday is gone by Monday. It is a transfer service, like DropMeFiles and temp.sh.
/// </para>
/// <para>
/// No accounts exist — anonymous is the only mode. Note the upload host (<c>litterbox.catbox.moe</c>)
/// and the link host (<c>litter.catbox.moe</c>) differ; the server names the latter in its reply, so
/// the link is used verbatim rather than rebuilt.
/// </para>
/// </summary>
public sealed class LitterboxPipeline : IFileHosterPipeline
{
    private const string Host = "https://litterbox.catbox.moe";
    private const string ApiUrl = Host + "/resources/internals/api.php";

    /// <summary>"Temporary uploads up to 1 GB are allowed", per its own upload page.</summary>
    private const long MaxFileSizeBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// The longest retention the host offers (its selector lists 1h, 12h, 24h, 72h). Always the
    /// longest: this app's links get posted, and every hour of retention is one the user might need.
    /// </summary>
    private const string Retention = "72h";

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public LitterboxPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload so the orchestration runs without the network.</summary>
    internal LitterboxPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "Litterbox";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous is the only mode — Litterbox has no accounts (catbox's userhash is not
    /// accepted here).</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Litterbox's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}). "
                + "catbox.moe keeps files permanently but caps them lower; a bigger file needs a different hoster.",
                null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

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

        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires in {Retention} — Litterbox offers no longer retention.");
        yield return new TransferCompleted(url!);
    }

    /// <summary>No accounts exist, so there is nothing to validate.</summary>
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
            "Litterbox has no accounts — use the built-in Anonymous option in the upload wizard. "
                + "(A catbox.moe account does not apply here.)"));
    }

    /// <summary>
    /// The response body is the share URL, or an error string — this API has no envelope. Internal for
    /// testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        string body = response.Body.Trim();

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Litterbox rejected the upload (HTTP {response.StatusCode}): {Snippet(body)}");
        }

        return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               && !body.Contains(' ', StringComparison.Ordinal)
            ? (body, null)
            : (null, $"Litterbox returned no link: {Snippet(body)}");
    }

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

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["reqtype"] = "fileupload",
            ["time"] = Retention,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
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
}
