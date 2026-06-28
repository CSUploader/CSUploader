// <copyright file="UpstorePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Upstore (upstore.net) upload pipeline — anonymous (not-logged-in) uploads only for now.
/// Verified against a live capture 2026-06-28.
/// <list type="bullet">
///   <item><b>Per-upload server assignment.</b> GET <c>https://upstore.net/</c> returns a
///   Dropzone <c>&lt;form class="dropzone" action="https://dNN.upstore.net/newupload/"&gt;</c>
///   whose upload host (<c>dNN</c>) rotates — so it's scraped fresh for each file, never cached.</item>
///   <item><b>The POST is a single-field multipart</b> (<c>name="file"</c>, the file) to the scraped
///   action — no token, no cookie, no other fields. The browser sends <c>X-Requested-With:
///   XMLHttpRequest</c> + <c>Accept: application/json</c> with the <c>upstore.net</c> Origin/Referer,
///   and the upload node is direct nginx (not Cloudflare-fronted).</item>
///   <item><b>Result.</b> JSON <c>{"hash":"&lt;code&gt;","name":…}</c>; the shareable link is
///   <c>https://upstore.net/&lt;code&gt;</c>.</item>
/// </list>
/// No login, no auth cache, no hashing. Login isn't wired up yet (see <see cref="CheckAccountAsync"/>).
/// </summary>
public sealed class UpstorePipeline : IFileHosterPipeline
{
    private const string Host = "https://upstore.net";
    private const string HomeUrl = Host + "/";
    private const string PublicUrlPrefix = Host + "/";

    /// <summary>Guest/free per-file cap — 2 GiB (confirmed by the account owner). The homepage's
    /// Dropzone <c>maxFilesize: 1024</c> (MiB) is only the client-side widget hint; the server
    /// actually accepts 2 GB for free + guest uploads, and we POST to it directly (not via Dropzone),
    /// so the server limit is what applies. Premium (not yet supported) lifts this to 5 GB.</summary>
    private const long AnonymousMaxFileSizeBytes = 2L * 1024 * 1024 * 1024;

    // The Dropzone form action points at the rotating upload host (dNN.upstore.net/newupload/).
    // Anchoring on "newupload" keeps us off the page's login/registration forms (/account/...),
    // and is robust to attribute ordering.
    private static readonly Regex _uploadActionRegex = new(
        """action=["']([^"']*newupload[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public UpstorePipeline()
    {
    }

    /// <summary>Test ctor — drives the homepage GET and the multipart upload from canned responses
    /// so the scrape/parse logic can be exercised without the network.</summary>
    internal UpstorePipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _uploadOverride = uploadOverride;
    }

    public string Name => "Upstore";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => AnonymousMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Upstore accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: anonymous per-file size cap ===
        // Failing fast here avoids pushing bytes that the guest tier is guaranteed to reject.
        if (ctx.FileSize > AnonymousMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Upstore's anonymous {ByteUnit.FromBytes(AnonymousMaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: scrape the rotating upload server from the homepage form action ===
        (string? actionUrl, string? scrapeError) = await FetchUploadActionAsync(ctx);
        if (actionUrl is null)
        {
            yield return new AttemptFailed(scrapeError ?? "Upstore upload form not found", null);
            yield break;
        }

        // === Step 2: upload ===
        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't
        // yield from inside the event handler) — same pattern as the other pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, actionUrl);

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

        // Let any transport fault propagate out of RunAsync to the shared retry layer (AttemptRunner):
        // a connect-phase failure or mid-send abort arrives as a safe-to-retry UploadBodyTransferException,
        // and re-running this pipeline scrapes a FRESH rotating upload node — the right recovery, and it
        // never double-creates because the body never finished. A SERVER VERDICT does NOT throw
        // (UploadMultipartAsync returns the snapshot), so it still flows through ParseUploadResponse.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Upstore login isn't wired up yet — uploads use the anonymous path. Surface a clear message
    /// rather than a silent failure if someone tries to add an Upstore account in Settings.
    /// </summary>
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
            "Upstore login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>GETs the homepage and scrapes the rotating Dropzone upload-form action URL.</summary>
    private async Task<(string? ActionUrl, string? Error)> FetchUploadActionAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx, HomeUrl);
        }
        catch (Exception ex)
        {
            return (null, "Upstore homepage fetch failed: " + ex.Message);
        }

        Match action = _uploadActionRegex.Match(snap.Body);
        return action.Success
            ? (action.Groups[1].Value, null)
            : (null, $"Upstore homepage did not contain an upload-form action URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl)
    {
        // The browser posts ONLY the file (Dropzone), no hidden fields, no cookie.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal);
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
            // The Dropzone client marks the upload as an XHR and asks for the JSON result shape.
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "application/json",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, actionUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            actionUrl,
            fileFieldName: "file",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 200 with JSON <c>{"hash":"&lt;code&gt;",…}</c>; the share link is
    /// <c>https://upstore.net/&lt;code&gt;</c>. A non-2xx, an unparseable body, or a missing hash
    /// (any error the server reports) surfaces as a failure with the body snippet.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Upstore upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        UpstoreUploadResult? result;
        try
        {
            result = JsonSerializer.Deserialize<UpstoreUploadResult>(response.Body);
        }
        catch
        {
            result = null;
        }

        if (result is { Error.Length: > 0 })
        {
            return (null, $"Upstore upload was rejected: {result.Error}");
        }

        if (result is null || string.IsNullOrEmpty(result.Hash))
        {
            return (null, $"Upstore upload did not return a file hash (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return (PublicUrlPrefix + result.Hash, null);
    }

    private Task<HttpResponseSnapshot> GetSnapshotAsync(AttemptContext ctx, string url)
        => _getSnapshotOverride is not null
            ? _getSnapshotOverride(url)
            : ctx.Handler.GetSnapshotAsync(url, headers: null, ctx.Cancellation);

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

    private sealed class UpstoreUploadResult
    {
        [JsonPropertyName("hash")] public string? Hash { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
