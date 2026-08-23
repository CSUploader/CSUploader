// <copyright file="VikingFileParallelPartsTests.cs" company="CSUploader">
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
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// VikingFile's parts actually fly together — and still assemble into the right file.
/// <para>
/// The reference conversion. Everything here is about what the sequential loop hid: a shared file
/// position, an absolute progress figure, and an error that used to stop the run simply because the
/// loop was sequential.
/// </para>
/// </summary>
public class VikingFileParallelPartsTests : IDisposable
{
    private const int PartSize = 4096;
    private const int Parts = 4;
    private const int FileBytes = PartSize * Parts;

    private static readonly string[] PartUrls =
    [
        "https://r2.invalid/p?partNumber=1",
        "https://r2.invalid/p?partNumber=2",
        "https://r2.invalid/p?partNumber=3",
        "https://r2.invalid/p?partNumber=4",
    ];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"csu-vfp-{Guid.NewGuid():N}.bin");

    public VikingFileParallelPartsTests()
    {
        File.WriteAllBytes(_path, Content);
    }

    /// <summary>
    /// The file's bytes, and deliberately APERIODIC. An <c>index % 251</c> pattern repeats every 251
    /// bytes, so a slice taken 251 bytes off compares EQUAL to the correct one and the wrong-offset
    /// bug goes undetected. xorshift32's period is 2^32-1, well past anything written here.
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

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static string InitJson() =>
        $$"""
        {"uploadId":"UP1","key":"K","partSize":{{PartSize}},"numberParts":{{Parts}},
         "urls":["{{PartUrls[0]}}","{{PartUrls[1]}}","{{PartUrls[2]}}","{{PartUrls[3]}}"]}
        """;

    private const string CompleteJson =
        """{"name":"f.bin","size":"16384","hash":"H","url":"https:\/\/vikingfile.com\/f\/H"}""";

    private AttemptContext Context(int degree, HttpHandler? handler = null) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _path,
        FileName = "f.bin",
        FileSize = FileBytes,
        HosterName = "VikingFile",
        Credentials = new FileHosterLoginDto { FileHosterName = "VikingFile" },
        Proxy = ProxyChoice.Direct,
        Handler = handler ?? new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        MaxParallelParts = degree,
        Cancellation = CancellationToken.None,
    };

    private VikingFilePipeline Pipeline(PutPartHandler putPart, List<string>? posts = null) => new(
        postFormOverride: (url, _) =>
        {
            posts?.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(
                200,
                url.Contains("complete-upload", StringComparison.Ordinal) ? CompleteJson : InitJson(),
                Array.Empty<string>()));
        },
        putPartOverride: putPart);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> events)
    {
        List<UploadEvent> collected = [];
        await foreach (UploadEvent e in events)
        {
            collected.Add(e);
        }

        return collected;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream sink = new();
        await stream.CopyToAsync(sink);
        return sink.ToArray();
    }

    [Fact]
    public async Task Parts_AreSentConcurrently_AndCompleteWithETagsInPartOrder()
    {
        // ETags must line up with part numbers or complete-multipart is rejected — and finishing
        // order is NOT part order once parts run together, so the parts here finish in reverse.
        int running = 0;
        int peak = 0;
        Lock sync = new();
        IReadOnlyDictionary<string, string>? completeForm = null;

        VikingFilePipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                if (url.Contains("complete-upload", StringComparison.Ordinal))
                {
                    completeForm = new Dictionary<string, string>(form, StringComparer.Ordinal);
                    return Task.FromResult(new HttpResponseSnapshot(200, CompleteJson, Array.Empty<string>()));
                }

                return Task.FromResult(new HttpResponseSnapshot(200, InitJson(), Array.Empty<string>()));
            },
            putPartOverride: async (url, partNumber, offset, length, body, report, ct) =>
            {
                lock (sync)
                {
                    peak = Math.Max(peak, ++running);
                }

                await Task.Delay((Parts - partNumber + 1) * 25, ct); // part 4 finishes FIRST
                lock (sync)
                {
                    running--;
                }

                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag-{partNumber}");
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 4), CancellationToken.None));

        Assert.True(peak > 1, "parts were still sent one at a time");
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.NotNull(completeForm);

        // The finalise form carries parts[i][PartNumber] / parts[i][ETag]; each index must hold its
        // OWN part's ETag despite the reversed completion order.
        for (int i = 0; i < Parts; i++)
        {
            Assert.Equal((i + 1).ToString(CultureInfo.InvariantCulture), completeForm![$"parts[{i}][PartNumber]"]);
            Assert.Equal($"etag-{i + 1}", completeForm[$"parts[{i}][ETag]"]);
        }
    }

    /// <summary>
    /// The shared-FileStream hazard, asserted on CONTENT. Recording the offset the pipeline passed
    /// proves only that its arithmetic ran; draining the body proves the stream actually delivers
    /// that region — which is exactly what a shared, position-advancing FileStream gets wrong.
    /// </summary>
    [Fact]
    public async Task EachPart_ReadsItsOwnBytes_EvenWhenConsumedInReverse()
    {
        ConcurrentDictionary<int, byte[]> bodies = new();

        // Explicit release gates, not Task.Delay: delays do not guarantee continuation ORDER, and
        // the whole point is that the parts consume their streams in reverse. Part N waits for N+1.
        TaskCompletionSource[] released = [.. Enumerable.Range(0, Parts + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        released[Parts].SetResult();

        VikingFilePipeline pipeline = Pipeline(async (url, partNumber, offset, length, body, report, ct) =>
        {
            // WaitAsync(ct) and a finally, both: if a read throws, the runner cancels its linked
            // token, and a bare await would strand every other worker instead of surfacing the fault.
            await released[partNumber].Task.WaitAsync(ct);
            try
            {
                bodies[partNumber] = await ReadAllAsync(body);
                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag-{partNumber}");
            }
            finally
            {
                released[partNumber - 1].TrySetResult();
            }
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 4), CancellationToken.None));

        for (int part = 1; part <= Parts; part++)
        {
            Assert.Equal(Expected((part - 1) * PartSize, PartSize), bodies[part]);
        }
    }

    [Fact]
    public async Task Progress_OnlyEverIncreases_AndReachesTheFileSize()
    {
        List<long> published = [];
        Lock sync = new();
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        handler.UploadProgress += (_, e) =>
        {
            lock (sync)
            {
                published.Add(e.BytesProcessed);
            }
        };

        VikingFilePipeline pipeline = Pipeline(async (url, partNumber, offset, length, body, report, ct) =>
        {
            // Report in steps, out of order across parts, which is what makes an absolute
            // basePosition + bytes figure lurch backwards.
            await Task.Delay((Parts - partNumber) * 10, ct);
            for (long sent = 1024; sent <= length; sent += 1024)
            {
                report(sent);
            }

            return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag-{partNumber}");
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 4, handler), CancellationToken.None));

        Assert.NotEmpty(published);
        Assert.Equal(published.OrderBy(x => x), published);
        Assert.Equal(FileBytes, published[^1]);
    }

    /// <summary>
    /// The production path, with NO part-PUT stub: the parts go through the real
    /// <c>HttpHandler.PutChunkAsync</c> against a draining message handler. This is the test that
    /// notices if the pipeline stops passing <c>reportPartProgress</c> — every other test here
    /// drives the seam directly and would stay green.
    /// </summary>
    [Fact]
    public async Task ThroughTheRealHandler_ProgressIsAggregated_NotAbsolutePerPart()
    {
        List<long> published = [];
        Lock sync = new();
        DrainingHandler transport = new();
        using HttpHandler handler = new(
            new HttpClient(transport), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        handler.UploadProgress += (_, e) =>
        {
            lock (sync)
            {
                published.Add(e.BytesProcessed);
            }
        };

        VikingFilePipeline pipeline = new(postFormOverride: (url, _) => Task.FromResult(
            new HttpResponseSnapshot(
                200,
                url.Contains("complete-upload", StringComparison.Ordinal) ? CompleteJson : InitJson(),
                Array.Empty<string>(),
                ETag: null)));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 4, handler), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.NotEmpty(published);

        // Aggregated: one monotonic stream ending at the file size. Absolute per-part figures would
        // exceed it (part 4 alone reports basePosition 12288 + 4096) and arrive out of order.
        Assert.Equal(published.OrderBy(x => x), published);
        Assert.Equal(FileBytes, published[^1]);
        Assert.All(published, value => Assert.InRange(value, 1, FileBytes));

        // Through the real transport, not a seam: the parts overlapped in flight, and each request
        // body carried its OWN region of the file. A pipeline that stopped calling
        // ParallelPartUploader keeps its aggregation but loses the first; a shared or wrongly
        // offset slice keeps both and loses the second.
        Assert.True(transport.Peak > 1, "the real transport saw the parts one at a time");
        Assert.Equal(Parts, transport.Bodies.Count);
        for (int part = 1; part <= Parts; part++)
        {
            Assert.Equal(Expected((part - 1) * PartSize, PartSize), transport.Bodies[part]);
        }
    }

    [Fact]
    public async Task AMismatchedPartMap_FailsBeforeSendingAnyBytes()
    {
        // The quiet failure mode: too FEW urls uploads a prefix of the file and still calls
        // complete-upload, publishing a truncated object that looks like a success.
        List<int> attempted = [];
        string shortInit =
            $$"""{"uploadId":"UP1","key":"K","partSize":{{PartSize}},"numberParts":2,"urls":["{{PartUrls[0]}}","{{PartUrls[1]}}"]}""";
        List<string> posts = [];

        VikingFilePipeline pipeline = new(
            postFormOverride: (url, _) =>
            {
                posts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, shortInit, Array.Empty<string>()));
            },
            putPartOverride: (url, partNumber, offset, length, body, report, ct) =>
            {
                attempted.Add(partNumber);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: "e"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 4), CancellationToken.None));

        Assert.Empty(attempted);                       // not a single byte sent
        Assert.Single(posts);                          // get-upload-url only — never complete-upload
        Assert.Contains("2 part URL(s)", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stands in for R2 at the bottom of the REAL <see cref="HttpHandler.PutChunkAsync"/> — the one
    /// layer the part overrides skip. It answers 200 with the right ETag, KEEPS the bytes rather
    /// than dropping them, and watches how many requests are in flight, so a test above it can check
    /// that the production transport carries each part's own region and carries them at once.
    /// <para>
    /// The delay is longest for the FIRST part, so the parts finish in REVERSE. Without that they
    /// complete almost instantly and often happen to finish in order — under which an absolute
    /// per-part figure looks monotonic too, and the test passes against the very bug it exists to
    /// catch. Forcing part 4 to finish first is what makes the two modes distinguishable.
    /// </para>
    /// </summary>
    private sealed class DrainingHandler : HttpMessageHandler
    {
        private readonly Lock _sync = new();
        private int _running;

        public ConcurrentDictionary<int, byte[]> Bodies { get; } = new();

        public int Peak { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int partNumber = int.Parse(
                request.RequestUri!.Query.Split("partNumber=")[1],
                CultureInfo.InvariantCulture);

            lock (_sync)
            {
                Peak = Math.Max(Peak, ++_running);
            }

            try
            {
                await Task.Delay((Parts - partNumber + 1) * 30, cancellationToken);

                if (request.Content is not null)
                {
                    using MemoryStream sink = new();
                    await request.Content.CopyToAsync(sink, cancellationToken);
                    Bodies[partNumber] = sink.ToArray();
                }

                HttpResponseMessage response = new(System.Net.HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
                response.Headers.TryAddWithoutValidation("ETag", $"\"etag-{partNumber}\"");
                return response;
            }
            finally
            {
                lock (_sync)
                {
                    _running--;
                }
            }
        }
    }

    /// <summary>
    /// The safety property: an un-opted-in hoster — or one the user has capped to 1 — must behave
    /// exactly as it did before, which means stopping at the first rejected part rather than
    /// uploading the rest and reporting the same error afterwards.
    /// </summary>
    [Fact]
    public async Task AtDegreeOne_StopsAtTheFirstRejectedPart()
    {
        List<int> attempted = [];

        VikingFilePipeline pipeline = Pipeline((url, partNumber, offset, length, body, report, ct) =>
        {
            attempted.Add(partNumber);
            return Task.FromResult(partNumber == 2
                ? new HttpResponseSnapshot(403, "<Error>AccessDenied</Error>", Array.Empty<string>())
                : new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: "e"));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 1), CancellationToken.None));

        Assert.Equal([1, 2], attempted);
        Assert.Contains("403", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtDegreeOne_SendsPartsOneAtATime()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();

        VikingFilePipeline pipeline = Pipeline(async (url, partNumber, offset, length, body, report, ct) =>
        {
            lock (sync)
            {
                peak = Math.Max(peak, ++running);
            }

            await Task.Delay(10, ct);
            lock (sync)
            {
                running--;
            }

            return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag-{partNumber}");
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 1), CancellationToken.None));

        Assert.Equal(1, peak);
    }
}
