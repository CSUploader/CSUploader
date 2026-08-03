// <copyright file="TempShPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// temp.sh — anonymous, no account, and about as simple as an upload gets: one multipart POST to
/// <c>https://temp.sh/upload</c> with the file in a field called <c>file</c>, and the response body is
/// the plain share URL (<c>https://temp.sh/&lt;code&gt;/&lt;name&gt;</c>). The host documents exactly
/// this on its homepage as <c>curl -F "file=@test.txt" https://temp.sh/upload</c>, and it was verified
/// with real bytes 2026-08-03.
/// <para>
/// <b>⚠ Files EXPIRE after 3 days</b> ("~everything is temporary~", in its own words) — so this is a
/// transfer service rather than storage, the same caveat DropMeFiles carries. 4 GB per file.
/// </para>
/// <para>
/// Found by questioning where the candidate list came from: it was derived from a debrid index, which
/// by construction lists only DOWNLOAD hosts, so transfer services and small drop hosts could never
/// appear in it however long the list was worked. That is why this arrived after the list was declared
/// exhausted.
/// </para>
/// </summary>
public sealed class TempShPipeline : IFileHosterPipeline
{
    private const string Host = "https://temp.sh";
    private const string UploadUrl = Host + "/upload";

    /// <summary>"Current file size limit is 4GB", per its About section. Decimal, which is the
    /// conservative reading — a binary 4 GiB would be 7% larger than what it says it takes.</summary>
    private const long MaxFileSizeBytes = 4L * 1000 * 1000 * 1000;

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public TempShPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload so the orchestration runs without the network.</summary>
    internal TempShPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "Temp.sh";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous is the only mode — there are no accounts at all.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds temp.sh's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
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

        // A transport fault propagates to the shared retry layer as a safe-to-retry
        // UploadBodyTransferException; a server verdict comes back as a snapshot and is parsed below.
        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires in 3 days — temp.sh keeps nothing longer.");
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
            "temp.sh has no accounts — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// The whole response is the share URL, or an error string. Anything that isn't an absolute URL is
    /// treated as the host refusing — it has no error envelope to parse. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        string body = response.Body.Trim();

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"temp.sh rejected the upload (HTTP {response.StatusCode}): {Snippet(body)}");
        }

        return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               && !body.Contains(' ', StringComparison.Ordinal)
            ? (body, null)
            : (null, $"temp.sh returned no link: {Snippet(body)}");
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
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, UploadUrl, new Dictionary<string, string>(StringComparer.Ordinal), headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            UploadUrl,
            fileFieldName: "file",
            extraFields: new Dictionary<string, string>(StringComparer.Ordinal),
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }
}
