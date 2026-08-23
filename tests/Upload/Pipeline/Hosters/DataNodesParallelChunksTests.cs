// <copyright file="DataNodesParallelChunksTests.cs" company="CSUploader">
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
/// DataNodes' chunks fly together. It is the odd one out among the parallel-safe hosters: there is
/// no per-part URL list at all. Every chunk goes to the SAME endpoint and says where it belongs with
/// an <c>X-Seek-To</c> header — which is precisely why the server does not depend on arrival order,
/// and why the host's own uploader sends up to ten at once.
/// </summary>
public class DataNodesParallelChunksTests : IDisposable
{
    private const int ChunkSize = 2048;
    private const int Chunks = 3;
    private const int FileBytes = 5120; // 2048 + 2048 + 1024: a SHORT final chunk

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"csu-dn-{Guid.NewGuid():N}");
    private readonly string _file;

    public DataNodesParallelChunksTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "release.r00");

        byte[] content = new byte[FileBytes];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = Pattern(i);
        }

        File.WriteAllBytes(_file, content);
    }

    private static byte Pattern(int index) => (byte)(index % 251);

    private static byte[] Expected(int from, int count)
        => [.. Enumerable.Range(from, count).Select(i => Pattern(i))];

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string StartJson = """{"plugin":"xfspro","url":"https://node42.datanodes.to/cgi-bin"}""";
    private const string ChunkOkJson = """{"status":"OK"}""";
    /// <summary>The anonymous finalise shape, copied from the live capture in
    /// <c>DataNodesPipelineTests</c> — a links object, not the array a guess would produce.</summary>
    private const string FinaliseJson = """
        {"links":{"download_link":"https://datanodes.to/abc123/release.r00","delete_link":"https://datanodes.to/abc123/release.r00?killcode=k1ll","html_code":"<a href=x>y</a>"}}
        """;

    private DataNodesPipeline Pipeline(
        Func<string, IReadOnlyDictionary<string, string>, long, Stream, Action<long>, CancellationToken, Task<HttpResponseSnapshot>> chunk)
        => new(
            postFormOverride: (url, form) => Task.FromResult(form["op"] switch
            {
                "start_upload" => new HttpResponseSnapshot(200, StartJson, Array.Empty<string>()),
                _ => new HttpResponseSnapshot(200, FinaliseJson, Array.Empty<string>()),
            }),
            chunkOverride: chunk,
            getOverride: null,
            chunkSizeBytes: ChunkSize);

    private AttemptContext Context(int degree) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "release.r00",
        FileSize = FileBytes,
        HosterName = "DataNodes",
        Credentials = new FileHosterLoginDto { FileHosterName = "DataNodes", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        MaxParallelParts = degree,
        Cancellation = default,
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

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream sink = new();
        await stream.CopyToAsync(sink);
        return sink.ToArray();
    }

    [Fact]
    public async Task Chunks_AreSentConcurrently_UnderOneSid()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();
        ConcurrentBag<string> sids = [];

        DataNodesPipeline pipeline = Pipeline(async (url, headers, length, body, report, ct) =>
        {
            sids.Add(headers["X-Upload-SID"]);
            lock (sync)
            {
                peak = Math.Max(peak, ++running);
            }

            await Task.Delay(30, ct);
            lock (sync)
            {
                running--;
            }

            return new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>());
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.True(peak > 1, "chunks were still sent one at a time");
        Assert.Empty(events.OfType<AttemptFailed>());

        // The SID groups the chunks of one upload; it must not vary per worker.
        Assert.Single(sids.Distinct());
    }

    [Fact]
    public async Task EachChunk_CarriesItsOwnSeekOffset_AndItsOwnBytes()
    {
        ConcurrentDictionary<long, byte[]> bySeek = new();

        // Force REVERSE consumption. Without it the chunks read their bodies in whatever order they
        // start, which is usually ascending — and a shared, position-advancing FileStream happens to
        // produce the right bytes in that case, so the test would pass against the bug it exists to
        // catch. Chunk N waits for chunk N+1 to finish reading.
        TaskCompletionSource[] released = [.. Enumerable.Range(0, Chunks + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        released[Chunks].SetResult();

        DataNodesPipeline pipeline = Pipeline(async (url, headers, length, body, report, ct) =>
        {
            long seek = long.Parse(headers["X-Seek-To"], CultureInfo.InvariantCulture);
            int index = (int)(seek / ChunkSize);

            await released[index + 1].Task.WaitAsync(ct);
            try
            {
                bySeek[seek] = await ReadAllAsync(body);
                return new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>());
            }
            finally
            {
                released[index].TrySetResult();
            }
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        // X-Seek-To is the whole protocol here: the bytes at each offset must be the file's bytes
        // at that offset, or the server reassembles a corrupted file without complaining.
        Assert.Equal(Expected(0, ChunkSize), bySeek[0]);
        Assert.Equal(Expected(ChunkSize, ChunkSize), bySeek[ChunkSize]);
        Assert.Equal(Expected(2 * ChunkSize, FileBytes - (2 * ChunkSize)), bySeek[2 * ChunkSize]);
    }

    /// <summary>
    /// A 2xx is not acceptance. This host can answer 200 with an error envelope, and under
    /// parallelism a silently-rejected chunk becomes a truncated file rather than a visible failure.
    /// </summary>
    [Fact]
    public async Task AChunkAnswered200ButNotOk_FailsTheUpload()
    {
        DataNodesPipeline pipeline = Pipeline((url, headers, length, body, report, ct) =>
            Task.FromResult(long.Parse(headers["X-Seek-To"], CultureInfo.InvariantCulture) == ChunkSize
                ? new HttpResponseSnapshot(200, """{"status":"ERROR","msg":"quota"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.Contains(
            "did not accept chunk",
            Assert.Single(events.OfType<AttemptFailed>()).Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZeroByteFile_StillSendsOneChunk()
    {
        // The chunk map yields one empty frame rather than none; a generic ceiling division would
        // produce zero chunks and upload nothing at all.
        string empty = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(empty, []);

        int calls = 0;
        DataNodesPipeline pipeline = Pipeline((url, headers, length, body, report, ct) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
        });

        AttemptContext ctx = Context(degree: 3) with { FilePath = empty, FileSize = 0 };
        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AtDegreeOne_SendsChunksOneAtATime()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();

        DataNodesPipeline pipeline = Pipeline(async (url, headers, length, body, report, ct) =>
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

            return new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>());
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 1), CancellationToken.None));

        Assert.Equal(1, peak);
    }
}
