// <copyright file="VikingFileParallelPartsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
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
        byte[] content = new byte[FileBytes];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = Pattern(i);
        }

        File.WriteAllBytes(_path, content);
    }

    /// <summary>251 is prime, so no two 4 KiB regions share a byte pattern — which is what lets a
    /// wrongly-sliced part be detected rather than merely suspected.</summary>
    private static byte Pattern(int index) => (byte)(index % 251);

    private static byte[] Expected(int from, int count)
        => [.. Enumerable.Range(from, count).Select(i => Pattern(i))];

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
        int running = 0;
        int peak = 0;
        Lock sync = new();
        List<string> posts = [];

        VikingFilePipeline pipeline = Pipeline(
            async (url, partNumber, offset, length, body, report, ct) =>
            {
                lock (sync)
                {
                    peak = Math.Max(peak, ++running);
                }

                await Task.Delay(30, ct);
                lock (sync)
                {
                    running--;
                }

                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag-{partNumber}");
            },
            posts);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 4), CancellationToken.None));

        Assert.True(peak > 1, "parts were still sent one at a time");
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, posts.Count); // get-upload-url, then complete-upload
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
