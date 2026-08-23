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
/// Per-hoster suites cover each protocol's own quirks. What they miss is the shared contract: a
/// conversion that forgets <c>reportPartProgress</c>, publishes an absolute <c>basePosition + bytes</c>,
/// or never routes through <c>ParallelPartUploader</c> at all would keep every one of them green.
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

        byte[] content = new byte[FileBytes];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(_file, content);
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
                chunkOverride: (url, headers, length, body, report, ct) =>
                {
                    int number = (int)(long.Parse(headers["X-Seek-To"], CultureInfo.InvariantCulture) / PartSize) + 1;
                    return ReportInStepsAsync(number, length, report, ct, null)
                        .ContinueWith(t => new HttpResponseSnapshot(200, """{"status":"OK"}""", Array.Empty<string>()), TaskScheduler.Default);
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

                    HttpResponseSnapshot response =
                        await ReportInStepsAsync(partNumber, length, report, ct, $"\"etag-{partNumber}\"");

                    lock (sync)
                    {
                        running--;
                    }

                    return response;
                });

            await DrainAsync(pipeline.RunAsync(Context("Storage.to", handler, Parts), CancellationToken.None));
        }

        Assert.True(peak > 1, "storage.to still sent its parts one at a time");
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

        using (handler)
        {
            UploadNowPipeline pipeline = new(
                apiOverride: (method, url, body, headers) => Task.FromResult(UploadNowStubs.Reply(url)),
                partOverride: async (url, offset, length, headers, openBody, report, ct) =>
                {
                    int partNumber = (int)(offset / PartSize) + 1;
                    lengths[partNumber] = length;

                    lock (sync)
                    {
                        peak = Math.Max(peak, ++running);
                    }

                    HttpResponseSnapshot response = await ReportInStepsAsync(partNumber, length, report, ct, "\"etag\"");

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
        AssertAggregated(published);
    }
}
