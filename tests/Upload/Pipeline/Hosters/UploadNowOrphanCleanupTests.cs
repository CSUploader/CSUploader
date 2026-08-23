// <copyright file="UploadNowOrphanCleanupTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// What happens to the parts already on the storage when an UploadNow multipart is abandoned.
/// <para>
/// Nothing collects them on its own. An incomplete multipart is invisible to the account's file list
/// and to the site's own UI, and the runner retries the whole attempt from a fresh initiate — so a
/// file that fails all four attempts leaves four sets of parts behind, billed to whoever owns the
/// bucket. UploadNow is the only one of the five converted hosters that CAN be cleaned up: it signs
/// its own storage requests through the host's signer, so <c>DELETE ?uploadId=</c> is available to
/// us. The other four are handed presigned PART urls and a complete endpoint, and nothing else.
/// </para>
/// </summary>
public class UploadNowOrphanCleanupTests : IDisposable
{
    private const int PartSize = 2048;
    private const int FileBytes = 5120; // 2048 + 2048 + 1024

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"csu-un-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly ConcurrentQueue<string> _calls = new();
    private IReadOnlyDictionary<string, string>? _abortHeaders;
    private readonly List<string> _logs = [];
    private readonly Mock<IAppLogger> _logger = new();

    public UploadNowOrphanCleanupTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "release.rar");
        File.WriteAllBytes(_file, new byte[FileBytes]);

        _logger
            .Setup(l => l.Log(
                It.IsAny<object?>(),
                It.IsAny<LogType>(),
                It.IsAny<string>(),
                It.IsAny<HttpTransaction?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()))
            .Callback((object? _, LogType _, string text, HttpTransaction? _, string _, string _, int _) =>
            {
                lock (_logs)
                {
                    _logs.Add(text);
                }
            });
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The DELETE the storage needs, if one was sent at all.</summary>
    private string? Abort => _calls.FirstOrDefault(c => c.StartsWith("DELETE ", StringComparison.Ordinal));

    private bool ComplainedAboutCleanup
    {
        get
        {
            lock (_logs)
            {
                return _logs.Exists(l => l.Contains("couldn't clean up", StringComparison.Ordinal));
            }
        }
    }

    private UploadNowPipeline Pipeline(
        Func<int, HttpResponseSnapshot>? part = null,
        Func<HttpMethod, string, HttpResponseSnapshot?>? api = null)
        => new(
            apiOverride: (method, url, body, headers) =>
            {
                _calls.Enqueue($"{method} {url}");
                if (method == HttpMethod.Delete)
                {
                    _abortHeaders = headers;
                }

                return Task.FromResult(api?.Invoke(method, url) ?? UploadNowStubs.Reply(url));
            },
            partOverride: (url, offset, length, headers, openBody, report, ct) =>
            {
                int number = (int)(offset / PartSize) + 1;
                report(length);
                return Task.FromResult(
                    part?.Invoke(number)
                    ?? new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, $"\"etag-{number}\""));
            },
            partSizeBytes: PartSize);

    private AttemptContext Context(CancellationToken ct = default) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "release.rar",
        FileSize = FileBytes,
        HosterName = "UploadNow",
        Credentials = new FileHosterLoginDto { FileHosterName = "UploadNow", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = _logger.Object,
        SpeedBudget = SpeedBudget.Unlimited,
        MaxParallelParts = 3,
        Cancellation = ct,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> events)
    {
        List<UploadEvent> collected = [];
        await foreach (UploadEvent e in events)
        {
            collected.Add(e);
        }

        return collected;
    }

    private static HttpResponseSnapshot Refused(int number) =>
        new(403, "<Error><Code>AccessDenied</Code></Error>", Array.Empty<string>());

    private static HttpResponseSnapshot Accepted(int number) =>
        new(200, string.Empty, Array.Empty<string>(), null, $"\"etag-{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");

    [Fact]
    public async Task ARefusedPart_AbortsTheMultipartItLeftBehind()
    {
        UploadNowPipeline pipeline = Pipeline(part: number => number == 2 ? Refused(number) : Accepted(number));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.NotNull(Abort);
        Assert.Contains("uploadId=UP-1", Abort!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The assembly is the last call, and the storage can refuse it inside a 200. Every part has
    /// landed by then — the largest orphan any failure path can leave.
    /// </summary>
    [Fact]
    public async Task ARefusedAssembly_AbortsTheMultipart()
    {
        UploadNowPipeline pipeline = Pipeline(
            api: (method, url) => method == HttpMethod.Post && url.Contains("uploadId=", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, "<Error><Code>InvalidPart</Code></Error>", Array.Empty<string>())
                : null);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.NotNull(Abort);
    }

    /// <summary>
    /// The other direction. After a successful assembly the object EXISTS and there is no upload id
    /// left to abort — a cleanup that fired unconditionally would put a pointless signed round trip
    /// on the end of every upload the app ever makes.
    /// </summary>
    [Fact]
    public async Task ASuccessfulUpload_SendsNoAbort()
    {
        List<UploadEvent> events = await DrainAsync(Pipeline().RunAsync(Context(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Null(Abort);
    }

    /// <summary>
    /// The cleanup's whole point. Cancelling is the commonest way to abandon a multipart, and the
    /// token that reports the cancellation is the same one the abort would naturally be sent on — so
    /// a cleanup written the obvious way is dead exactly when it is needed most. It gets its own
    /// bounded token instead.
    /// </summary>
    [Fact]
    public async Task ACancelledUpload_StillSendsTheAbort()
    {
        using CancellationTokenSource cts = new();
        UploadNowPipeline pipeline = Pipeline(part: number =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DrainAsync(pipeline.RunAsync(Context(cts.Token), cts.Token)));

        Assert.NotNull(Abort);
    }

    /// <summary>
    /// The signature is not decoration: the storage rejects a DELETE signed for a different method,
    /// query, or header set, and a rejected abort orphans the parts exactly as thoroughly as no
    /// abort at all.
    /// <para>
    /// The method and query cannot be read back from the signer call — it is handed a string-to-sign
    /// carrying only a HASH of the canonical request, so asserting on them would mean recomputing
    /// that hash here and comparing the production code to itself. What IS observable is the header
    /// set the signature commits to, and it must be the abort's own: borrowing a part's would carry
    /// <c>content-md5</c>, which this request neither signs nor sends.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheAbort_IsSignedForItsOwnEmptyBodiedRequest()
    {
        UploadNowPipeline pipeline = Pipeline(part: number => number == 1 ? Refused(number) : Accepted(number));

        await DrainAsync(pipeline.RunAsync(Context(), CancellationToken.None));

        Assert.NotNull(Abort);
        Assert.StartsWith("DELETE ", Abort!, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(_abortHeaders);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=2f488bd324502ec2/", headers["Authorization"], StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=host;x-amz-date,", headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            headers["x-amz-content-sha256"]);
    }

    /// <summary>
    /// Cleanup is best-effort and it runs in a <c>finally</c>. If its own failure escaped, or
    /// replaced the error the caller already holds, a refused part would reach the user as a cleanup
    /// problem and the actual cause would never be reported.
    /// </summary>
    [Fact]
    public async Task AnAbortThatFails_LeavesTheRealErrorIntact()
    {
        UploadNowPipeline pipeline = Pipeline(
            part: number => number == 2 ? Refused(number) : Accepted(number),
            api: (method, url) => method == HttpMethod.Delete
                ? new HttpResponseSnapshot(500, "boom", Array.Empty<string>())
                : null);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(), CancellationToken.None));

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("refused part 2", failed.Reason, StringComparison.Ordinal);
        Assert.True(ComplainedAboutCleanup, "the failed cleanup left no trace in the log");
    }

    /// <summary>
    /// 404 is the storage saying the upload id is already gone — completed, or aborted by an earlier
    /// pass. That is what the cleanup wanted, not something to put in front of the user.
    /// </summary>
    [Fact]
    public async Task AnAbortAnsweredNotFound_IsNotReportedAsAProblem()
    {
        UploadNowPipeline pipeline = Pipeline(
            part: number => number == 2 ? Refused(number) : Accepted(number),
            api: (method, url) => method == HttpMethod.Delete
                ? new HttpResponseSnapshot(404, "<Error><Code>NoSuchUpload</Code></Error>", Array.Empty<string>())
                : null);

        await DrainAsync(pipeline.RunAsync(Context(), CancellationToken.None));

        Assert.NotNull(Abort);
        Assert.False(ComplainedAboutCleanup, "a 404 from the abort was reported as a cleanup failure");
    }
}
