// <copyright file="ConvertedPipelineContractTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
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
/// The properties EVERY converted pipeline must hold, asserted once per hoster rather than trusted
/// to have survived four copy-and-paste conversions.
/// <para>
/// Per-hoster suites cover each protocol's own quirks, and three of them (VikingFile, Hostize,
/// DataNodes) already read and compare their part bodies. What no per-hoster suite covers is the
/// shared contract, asserted here for all five in one place: that progress is AGGREGATED rather than
/// published as an absolute <c>basePosition + bytes</c>. The storage.to and UploadNow cases also
/// require the parts to overlap in flight and to carry their own bytes, because those two suites
/// did not.
/// </para>
/// <para>
/// Note what "aggregated" and "overlapping" are: observable PROPERTIES, not evidence that any
/// particular helper was called. A hand-rolled concurrent scheduler around the same correct workers
/// would satisfy every case here, and for the three progress-only hosters so would a sequential
/// loop. Nothing in this class pins <c>ParallelPartUploader</c> itself.
/// </para>
/// <para>
/// <b>What these do NOT prove.</b> Every case injects a part override, so the real
/// <c>HttpHandler.PutChunkAsync</c> branch is bypassed — deleting <c>reportPartProgress:</c> from a
/// pipeline's PRODUCTION call would still leave them green. They pin the seam-to-aggregator path,
/// not the transport join. VikingFile has a separate real-handler test
/// (<c>ThroughTheRealHandler_ProgressIsAggregated</c>); the other four are uncovered. Task 9 has
/// LANDED and strengthened that same VikingFile test rather than adding a second hoster, so the
/// other four transports remain unproven today. This comment exists so nobody reads the class as
/// covering them.
/// </para>
/// </summary>
public class ConvertedPipelineContractTests : IDisposable
{
    private const int PartSize = 2048;
    private const int Parts = 3;
    private const int FileBytes = 5120; // 2048 + 2048 + 1024: a SHORT final part

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"csu-contract-{Guid.NewGuid():N}");
    private readonly string _file;

    public ConvertedPipelineContractTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "probe.bin");

        File.WriteAllBytes(_file, Content);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AttemptContext Context(string hoster, HttpHandler handler, int degree) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.bin",
        FileSize = FileBytes,
        HosterName = hoster,
        Credentials = new FileHosterLoginDto { FileHosterName = hoster, IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = handler,
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        MaxParallelParts = degree,
        Cancellation = default,
    };

    private static async Task DrainAsync(IAsyncEnumerable<UploadEvent> events)
    {
        await foreach (UploadEvent _ in events)
        {
            // drain
        }
    }

    /// <summary>
    /// Reports each part's bytes in steps, deliberately OUT OF ORDER across parts — which is the
    /// condition under which an absolute figure lurches backwards and an aggregated one does not.
    /// </summary>
    private static async Task<HttpResponseSnapshot> ReportInStepsAsync(
        int partNumber, long length, Action<long> report, CancellationToken ct, string? etag)
    {
        await Task.Delay((Parts - partNumber + 1) * 15, ct);
        for (long sent = 512; sent <= length; sent += 512)
        {
            report(sent);
        }

        report(length);
        return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, etag);
    }

    private static (HttpHandler Handler, List<long> Published, Lock Sync) Recorder()
    {
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        List<long> published = [];
        Lock sync = new();
        handler.UploadProgress += (_, e) =>
        {
            lock (sync)
            {
                published.Add(e.BytesProcessed);
            }
        };

        return (handler, published, sync);
    }

    /// <summary>
    /// The probe file's bytes, and deliberately APERIODIC. An <c>index % 251</c> pattern repeats
    /// every 251 bytes, so a part that opened its slice 251 bytes early or late would compare EQUAL
    /// to the correct one - the wrong-offset bug these assertions exist to catch would pass. xorshift32
    /// has a period of 2^32-1, which is longer than any file this suite writes.
    /// </summary>
    private static readonly byte[] Content = BuildContent();

    private static byte[] BuildContent()
    {
        byte[] bytes = new byte[FileBytes];
        uint state = 0x9E3779B9; // any fixed seed - what matters is that it is FIXED
        for (int i = 0; i < bytes.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }

    private static byte[] Expected(int from, int count) => Content.AsSpan(from, count).ToArray();

    /// <summary>
    /// Waits until the NEXT part has finished reading, so this one reads out of ascending order.
    /// <para>
    /// BOUNDED, deliberately. The chained gates need every part to hold a runner slot at the same
    /// time, so a regression that drops the effective degree below the part count would otherwise
    /// hang the suite instead of failing it - and a test that hangs on the bug it exists to catch is
    /// worth less than no test at all.
    /// </para>
    /// </summary>
    private static async Task AwaitSuccessorAsync(TaskCompletionSource[] released, int index, CancellationToken ct)
    {
        try
        {
            await released[index].Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"part {(index + 1).ToString(CultureInfo.InvariantCulture)} never finished reading, "
                + $"so part {index.ToString(CultureInfo.InvariantCulture)} is still waiting for it: "
                + "the parts are not all in flight at once, and the reverse-order read this "
                + "assertion depends on cannot happen.");
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream sink = new();
        await stream.CopyToAsync(sink);
        return sink.ToArray();
    }

    /// <summary>Each part must carry ITS OWN region, including the short final one. Recording the
    /// offset a pipeline passed proves only that its arithmetic ran.</summary>
    private static void AssertEachPartCarriedItsOwnBytes(IDictionary<int, byte[]> bodies)
    {
        Assert.Equal(Parts, bodies.Count);
        Assert.Equal(Expected(0, PartSize), bodies[1]);
        Assert.Equal(Expected(PartSize, PartSize), bodies[2]);
        Assert.Equal(Expected(2 * PartSize, FileBytes - (2 * PartSize)), bodies[3]);
    }

    private static void AssertAggregated(List<long> published)
    {
        Assert.NotEmpty(published);

        // Monotonic and ending at the file size. Absolute per-part figures would arrive out of
        // order and the last one would depend on which part happened to finish last.
        Assert.Equal(published.OrderBy(x => x), published);
        Assert.Equal(FileBytes, published[^1]);
        Assert.All(published, value => Assert.InRange(value, 1, FileBytes));
    }

    [Fact]
    public async Task VikingFile_AggregatesProgressAcrossParts()
    {
        (HttpHandler handler, List<long> published, _) = Recorder();
        string init =
            $$"""{"uploadId":"U","key":"K","partSize":{{PartSize}},"numberParts":{{Parts}},"urls":["https://r2.invalid/1","https://r2.invalid/2","https://r2.invalid/3"]}""";

        using (handler)
        {
            VikingFilePipeline pipeline = new(
                postFormOverride: (url, _) => Task.FromResult(new HttpResponseSnapshot(
                    200,
                    url.Contains("complete-upload", StringComparison.Ordinal)
                        ? """{"name":"f","size":"5120","hash":"H","url":"https://vikingfile.com/f/H"}"""
                        : init,
                    Array.Empty<string>())),
                putPartOverride: (url, partNumber, offset, length, body, report, ct) =>
                    ReportInStepsAsync(partNumber, length, report, ct, $"\"etag-{partNumber}\""));

            await DrainAsync(pipeline.RunAsync(Context("VikingFile", handler, Parts), CancellationToken.None));
        }

        AssertAggregated(published);
    }

    [Fact]
    public async Task Hostize_AggregatesProgressAcrossParts()
    {
        (HttpHandler handler, List<long> published, _) = Recorder();
        string ticket =
            $$"""{"id":"S","tickets":[{"partSize":{{PartSize}},"uploadId":"U","partUrls":[{"partNumber":1,"url":"https://s3.invalid/1"},{"partNumber":2,"url":"https://s3.invalid/2"},{"partNumber":3,"url":"https://s3.invalid/3"}]}]}""";

        using (handler)
        {
            HostizePipeline pipeline = new(
                (url, _) => Task.FromResult(new HttpResponseSnapshot(
                    url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                    url.EndsWith("/request", StringComparison.Ordinal)
                        ? ticket
                        : """{"id":"S","expiresAt":"2026-08-09T16:05:49.437Z","size":"5120","downloads":0,"uploaded":true}""",
                    Array.Empty<string>())),
                (url, partNumber, offset, length, body, report, ct) =>
                    ReportInStepsAsync(partNumber, length, report, ct, null));

            await DrainAsync(pipeline.RunAsync(Context("Hostize", handler, Parts), CancellationToken.None));
        }

        AssertAggregated(published);
    }

    [Fact]
    public async Task DataNodes_AggregatesProgressAcrossChunks()
    {
        (HttpHandler handler, List<long> published, _) = Recorder();

        using (handler)
        {
            DataNodesPipeline pipeline = new(
                postFormOverride: (url, form) => Task.FromResult(form["op"] switch
                {
                    "start_upload" => new HttpResponseSnapshot(200, """{"plugin":"xfspro","url":"https://node42.datanodes.to/cgi-bin"}""", Array.Empty<string>()),
                    _ => new HttpResponseSnapshot(200, """{"links":{"download_link":"https://datanodes.to/a/b","delete_link":"https://datanodes.to/a/b?killcode=k","html_code":"x"}}""", Array.Empty<string>()),
                }),
                chunkOverride: async (url, headers, length, body, report, ct) =>
                {
                    // await, not ContinueWith: a continuation runs regardless of the antecedent's
                    // outcome, so a fault or cancellation inside ReportInStepsAsync would be turned
                    // into a success response and the test would pass through the failure.
                    int number = (int)(long.Parse(headers["X-Seek-To"], CultureInfo.InvariantCulture) / PartSize) + 1;
                    await ReportInStepsAsync(number, length, report, ct, null);
                    return new HttpResponseSnapshot(200, """{"status":"OK"}""", Array.Empty<string>());
                },
                getOverride: null,
                chunkSizeBytes: PartSize);

            await DrainAsync(pipeline.RunAsync(Context("DataNodes", handler, Parts), CancellationToken.None));
        }

        AssertAggregated(published);
    }

    /// <summary>
    /// storage.to, which its own suite never exercises as parallel: that context leaves
    /// <c>MaxParallelParts</c> at its default of 1 and its seam ignores the body, the reporter and
    /// the token entirely. A regression to the old shared <c>FileStream</c> or the sequential loop
    /// would pass every test over there.
    /// </summary>
    [Fact]
    public async Task StorageTo_SendsPartsConcurrently_AndAggregatesProgress()
    {
        (HttpHandler handler, List<long> published, _) = Recorder();
        int running = 0;
        int peak = 0;
        Lock sync = new();
        ConcurrentDictionary<int, byte[]> bodies = new();

        // Forced REVERSE consumption: part N waits for N+1 to finish reading. Ascending order is
        // what a shared advancing FileStream happens to get right, so it would hide the bug.
        TaskCompletionSource[] released = [.. Enumerable.Range(0, Parts + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        released[Parts].SetResult();

        // Literal 2048/3 rather than interpolated: the payload ends in }}} , which fights raw-string
        // interpolation harder than it is worth. Asserted against the constants below.
        Assert.Equal(2048, PartSize);
        Assert.Equal(3, Parts);
        const string Init =
            """{"success":true,"results":{"0":{"success":true,"type":"multipart","upload_id":"UP","r2_key":"K","part_size":2048,"total_parts":3,"initial_urls":{"1":"https://r2.mock/1","2":"https://r2.mock/2","3":"https://r2.mock/3"},"owner_token":"o"}}}""";

        using (handler)
        {
            StorageToPipeline pipeline = new(
                getOverride: _ => new HttpResponseSnapshot(200, StorageToStubs.HomeHtml, []),
                postJsonOverride: (url, body, headers) => new HttpResponseSnapshot(
                    200,
                    url.Contains("confirm", StringComparison.Ordinal) ? StorageToStubs.ConfirmJson : Init,
                    []),
                putOverride: (filePath, url, contentType, progress) =>
                    new HttpResponseSnapshot(200, string.Empty, []),
                putPartOverride: async (url, partNumber, offset, length, body, report, ct) =>
                {
                    lock (sync)
                    {
                        peak = Math.Max(peak, ++running);
                    }

                    await AwaitSuccessorAsync(released, partNumber, ct);
                    try
                    {
                        bodies[partNumber] = await ReadAllAsync(body);
                        return await ReportInStepsAsync(partNumber, length, report, ct, $"\"etag-{partNumber}\"");
                    }
                    finally
                    {
                        released[partNumber - 1].TrySetResult();
                        lock (sync)
                        {
                            running--;
                        }
                    }
                });

            await DrainAsync(pipeline.RunAsync(Context("Storage.to", handler, Parts), CancellationToken.None));
        }

        Assert.True(peak > 1, "storage.to still sent its parts one at a time");
        AssertEachPartCarriedItsOwnBytes(bodies);
        AssertAggregated(published);
    }

    /// <summary>
    /// UploadNow at a REAL multipart size. Its own suite runs a 2,048-byte file against a 64 MiB
    /// part, which is always exactly one part — so nothing there could observe concurrency, a short
    /// final part, independent MD5 passes, or ETag ordering at all.
    /// </summary>
    [Fact]
    public async Task UploadNow_SendsSeveralPartsConcurrently_AndAggregatesProgress()
    {
        (HttpHandler handler, List<long> published, _) = Recorder();
        int running = 0;
        int peak = 0;
        Lock sync = new();
        ConcurrentDictionary<int, long> lengths = new();
        ConcurrentDictionary<int, byte[]> bodies = new();
        ConcurrentDictionary<int, string> signedMd5 = new();
        string? completeBody = null;

        using (handler)
        {
            UploadNowPipeline pipeline = new(
                apiOverride: (method, url, body, headers) =>
                {
                    if (url.EndsWith("?uploadId=UP-1", StringComparison.Ordinal) || (body?.Contains("CompleteMultipartUpload", StringComparison.Ordinal) ?? false))
                    {
                        completeBody = body;
                    }

                    return Task.FromResult(UploadNowStubs.Reply(url));
                },
                partOverride: async (url, offset, length, headers, openBody, report, ct) =>
                {
                    int partNumber = (int)(offset / PartSize) + 1;
                    lengths[partNumber] = length;
                    signedMd5[partNumber] = headers["Content-MD5"];

                    // openBody(), not body: the retry re-invokes this delegate, so the seam hands a
                    // FACTORY. Reading it also proves the slice is this part's own region.
                    bodies[partNumber] = await ReadAllAsync(openBody());

                    lock (sync)
                    {
                        peak = Math.Max(peak, ++running);
                    }

                    HttpResponseSnapshot response = await ReportInStepsAsync(partNumber, length, report, ct, $"\"etag-{partNumber}\"");

                    lock (sync)
                    {
                        running--;
                    }

                    return response;
                },
                partSizeBytes: PartSize);

            await DrainAsync(pipeline.RunAsync(Context("UploadNow", handler, Parts), CancellationToken.None));
        }

        Assert.True(peak > 1, "UploadNow still sent its parts one at a time");
        Assert.Equal(Parts, lengths.Count);
        Assert.Equal(FileBytes - (2 * PartSize), lengths[Parts]); // the SHORT final part
        AssertEachPartCarriedItsOwnBytes(bodies);
        AssertAggregated(published);

        // The MD5 pre-pass and the upload pass used to share one FileStream; each part's signed
        // hash must be of the bytes that part actually sent.
        for (int part = 1; part <= Parts; part++)
        {
            Assert.Equal(
                Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(bodies[part])),
                signedMd5[part]);
        }

        // Distinct ETags, in PART order, so the completion body proves ordering rather than
        // accidentally agreeing because every part returned the same value.
        Assert.NotNull(completeBody);
        int first = completeBody!.IndexOf("etag-1", StringComparison.Ordinal);
        int second = completeBody.IndexOf("etag-2", StringComparison.Ordinal);
        int third = completeBody.IndexOf("etag-3", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third, $"ETags out of part order: {completeBody}");
    }
}
