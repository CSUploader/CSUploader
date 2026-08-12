// <copyright file="TmpFilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// tmpfiles.org — anonymous, no accounts, and the rare case of a host that documents its own API
/// (<c>https://tmpfiles.org/api</c>). One multipart POST to <c>/api/v1/upload</c> with the file in a
/// field called <c>file</c> → <c>{"status":"success","data":{"url":"https://tmpfiles.org/{id}/{name}"}}</c>.
/// Verified with real bytes 2026-08-03.
/// <para>
/// <b>⚠ The default retention is ONE HOUR.</b> The documented <c>expire</c> field takes 60–172800
/// seconds, and omitting it silently takes the minimum end of that range — measured, not assumed: an
/// upload with <c>expire=172800</c> reports "File expires in 47 hours and 59 minutes" while the same
/// upload without it reports "59 minutes". This pipeline always sends the maximum, which is 48× the
/// retention for one field. Litterbox sets the same trap with <c>fileNameLength</c>; assume any
/// optional field on a drop host encodes a worse default.
/// </para>
/// <para>
/// <b>The link is the API's, verbatim.</b> Its <c>/dl/</c> direct-download form carries a per-request
/// token (<c>/dl/&lt;timestamp&gt;.&lt;hash&gt;/&lt;id&gt;/&lt;name&gt;</c>) that the page mints, so
/// there is no stable direct link to prefer — the page URL is what a user shares, and it renders the
/// filename, size and remaining time.
/// </para>
/// <para>100 MB per file. A transfer service, like temp.sh and Litterbox — not storage.</para>
/// </summary>
public sealed class TmpFilesPipeline : IFileHosterPipeline
{
    private const string Host = "https://tmpfiles.org";
    private const string UploadUrl = Host + "/api/v1/upload";

    /// <summary>"max 100 MB", per its API page.</summary>
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>
    /// The documented maximum (<c>expire</c> accepts 60–172800 seconds). 172800 = 48 hours; the
    /// default when the field is absent is 3600 = one hour.
    /// </summary>
    private const string ExpireSeconds = "172800";

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public TmpFilesPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload so the orchestration runs without the network.</summary>
    internal TmpFilesPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "TmpFiles";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>48 hours - the documented maximum this app sends (<c>expire=172800</c>). Its own
    /// default is ONE hour, so this figure is a property of what we ask for.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => FileRetention.AfterUpload(TimeSpan.FromHours(48));

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous is the only mode — there are no accounts.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>tmpfiles.org has no login anywhere on the site, so the Add Account dialog leaves it out
    /// of its hoster list — there is nothing to add.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds tmpfiles.org's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
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

        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires in 48 hours — the longest tmpfiles.org offers.");
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
            "tmpfiles.org has no accounts — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// Reads <c>{"status":"success","data":{"url":…}}</c>. The status is checked explicitly rather than
    /// trusting the presence of a url, so an error envelope that happens to carry one can't read as
    /// success. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"tmpfiles.org rejected the upload (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out JsonElement status)
                && string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("url", out JsonElement url)
                && url.GetString() is { Length: > 0 } link)
            {
                return (link, null);
            }
        }
        catch (JsonException)
        {
            return (null, $"tmpfiles.org returned an unreadable response: {Snippet(response.Body)}");
        }

        return (null, $"tmpfiles.org returned no link: {Snippet(response.Body)}");
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
            ["expire"] = ExpireSeconds,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
            ["Accept"] = "application/json",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, UploadUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            UploadUrl,
            fileFieldName: "file",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }
}
