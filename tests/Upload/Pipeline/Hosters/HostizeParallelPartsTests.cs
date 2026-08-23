// <copyright file="HostizeParallelPartsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Hostize's parts fly together. It diverges from the VikingFile template in two ways that matter:
/// it collects NO ETags — <c>complete</c> takes only the share id and the server finalises the
/// multipart itself — and its ticket carries explicit part numbers, so a part's byte offset comes
/// from its number rather than its position in the array.
/// </summary>
public class HostizeParallelPartsTests : IDisposable
{
    private const int PartSize = 2048;
    private const int Parts = 3;
    private const int FileBytes = 5120; // 2048 + 2048 + 1024: a SHORT final part

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"csu-hz-{Guid.NewGuid():N}.rar");

    public HostizeParallelPartsTests()
    {
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
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    private const string TicketJson = """
        {"id":"SHARE","tickets":[{"partSize":2048,"uploadId":"U","partUrls":[{"partNumber":1,"url":"https://s3.invalid/a?p=1"},{"partNumber":2,"url":"https://s3.invalid/a?p=2"},{"partNumber":3,"url":"https://s3.invalid/a?p=3"}]}]}
        """;

    private const string CompletedJson = """
        {"id":"SHARE","expiresAt":"2026-08-09T16:05:49.437Z","size":"5120","downloads":0,"uploaded":true}
        """;

    private AttemptContext Context(int degree) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = FileBytes,
        HosterName = "Hostize",
        Credentials = new FileHosterLoginDto { FileHosterName = "Hostize", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        MaxParallelParts = degree,
        Cancellation = default,
    };

    private static HostizePipeline Pipeline(PutPartHandler putPart, List<string>? bodies = null) => new(
        (url, json) =>
        {
            bodies?.Add(json);
            return Task.FromResult(new HttpResponseSnapshot(
                url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                url.EndsWith("/request", StringComparison.Ordinal) ? TicketJson : CompletedJson,
                Array.Empty<string>()));
        },
        putPart);

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
    public async Task Parts_AreSentConcurrently_AndPublishWithoutETags()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();

        HostizePipeline pipeline = Pipeline(async (url, partNumber, offset, length, body, report, ct) =>
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

            // Deliberately NO ETag: Hostize's complete call takes only the share id, so a
            // conversion that copied VikingFile's ETag requirement would fail here.
            return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>());
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.True(peak > 1, "parts were still sent one at a time");
        Assert.Empty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task EachPart_ReadsItsOwnBytes_IncludingTheShortFinalPart()
    {
        ConcurrentDictionary<int, byte[]> bodies = new();

        // Reverse consumption, forced rather than hoped for.
        TaskCompletionSource[] released = [.. Enumerable.Range(0, Parts + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        released[Parts].SetResult();

        HostizePipeline pipeline = Pipeline(async (url, partNumber, offset, length, body, report, ct) =>
        {
            await released[partNumber].Task.WaitAsync(ct);
            try
            {
                bodies[partNumber] = await ReadAllAsync(body);
                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>());
            }
            finally
            {
                released[partNumber - 1].TrySetResult();
            }
        });

        await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.Equal(Expected(0, PartSize), bodies[1]);
        Assert.Equal(Expected(PartSize, PartSize), bodies[2]);
        Assert.Equal(Expected(2 * PartSize, FileBytes - (2 * PartSize)), bodies[3]);
    }

    /// <summary>
    /// A part's byte range comes from its own <c>partNumber</c>, not its position in the array.
    /// The two agree in every fixture the live service has produced, which is exactly why this
    /// needs its own test: a ticket listed in a different order would otherwise mis-slice the file
    /// silently, and every other test here would still pass.
    /// </summary>
    [Fact]
    public async Task APartsOffsetComesFromItsNumber_NotItsPositionInTheTicket()
    {
        const string ReversedTicket = """
            {"id":"SHARE","tickets":[{"partSize":2048,"uploadId":"U","partUrls":[{"partNumber":3,"url":"https://s3.invalid/a?p=3"},{"partNumber":2,"url":"https://s3.invalid/a?p=2"},{"partNumber":1,"url":"https://s3.invalid/a?p=1"}]}]}
            """;

        ConcurrentDictionary<int, byte[]> bodies = new();
        HostizePipeline pipeline = new(
            (url, _) => Task.FromResult(new HttpResponseSnapshot(
                url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                url.EndsWith("/request", StringComparison.Ordinal) ? ReversedTicket : CompletedJson,
                Array.Empty<string>())),
            async (url, partNumber, offset, length, body, report, ct) =>
            {
                bodies[partNumber] = await ReadAllAsync(body);
                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>());
            });

        await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        // Each part still carries ITS OWN region despite the ticket listing them backwards.
        Assert.Equal(Expected(0, PartSize), bodies[1]);
        Assert.Equal(Expected(PartSize, PartSize), bodies[2]);
        Assert.Equal(Expected(2 * PartSize, FileBytes - (2 * PartSize)), bodies[3]);
    }

    /// <summary>
    /// A part map with the right COUNT but the wrong numbers. [1, 1, 3] passes a size check, sends
    /// part 1's bytes to two different presigned URLs and never sends part 2 — and because complete
    /// takes only the share id, the server would publish that corruption without a word.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 3)]   // duplicate, with a gap
    [InlineData(1, 2, 4)]   // out of range
    public async Task AMalformedPartMap_IsRejectedBeforeSendingAnyBytes(int a, int b, int c)
    {
        List<int> attempted = [];
        string ticket = $$"""{"id":"SHARE","tickets":[{"partSize":2048,"uploadId":"U","partUrls":[{"partNumber":{{a}},"url":"https://s3.invalid/a"},{"partNumber":{{b}},"url":"https://s3.invalid/b"},{"partNumber":{{c}},"url":"https://s3.invalid/c"}]}]}""";

        HostizePipeline pipeline = new(
            (url, _) => Task.FromResult(new HttpResponseSnapshot(
                url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                url.EndsWith("/request", StringComparison.Ordinal) ? ticket : CompletedJson,
                Array.Empty<string>())),
            (url, partNumber, offset, length, body, report, ct) =>
            {
                attempted.Add(partNumber);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.Empty(attempted);
        Assert.Contains("malformed part map", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ticket entry that genuinely OMITS partNumber, which the theory above cannot express — it
    /// always emits the property, so restoring the old "fall back to array position" behaviour would
    /// leave that test green.
    /// </summary>
    [Fact]
    public async Task ATicketEntryMissingItsPartNumber_IsRejectedRatherThanNumberedByPosition()
    {
        List<int> attempted = [];
        const string TicketMissingANumber = """{"id":"SHARE","tickets":[{"partSize":2048,"uploadId":"U","partUrls":[{"url":"https://s3.invalid/a"},{"partNumber":2,"url":"https://s3.invalid/b"},{"partNumber":3,"url":"https://s3.invalid/c"}]}]}""";

        HostizePipeline pipeline = new(
            (url, _) => Task.FromResult(new HttpResponseSnapshot(
                url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                url.EndsWith("/request", StringComparison.Ordinal) ? TicketMissingANumber : CompletedJson,
                Array.Empty<string>())),
            (url, partNumber, offset, length, body, report, ct) =>
            {
                attempted.Add(partNumber);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        Assert.Empty(attempted);
        Assert.Contains("malformed part map", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTicketRequest_DeclaresTheConcurrencyItWillActuallyUse()
    {
        // The site's own uploader sends "concurrency":4. Omitting it was right while parts went up
        // one at a time; now it should say what it means to do.
        List<string> bodies = [];
        HostizePipeline pipeline = Pipeline(
            (url, partNumber, offset, length, body, report, ct) =>
                Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())),
            bodies);

        await DrainAsync(pipeline.RunAsync(Context(degree: 3), CancellationToken.None));

        using JsonDocument request = JsonDocument.Parse(bodies[0]);
        Assert.Equal(3, request.RootElement.GetProperty("concurrency").GetInt32());
    }

    [Fact]
    public async Task AtDegreeOne_StopsAtTheFirstRejectedPart()
    {
        List<int> attempted = [];

        HostizePipeline pipeline = Pipeline((url, partNumber, offset, length, body, report, ct) =>
        {
            attempted.Add(partNumber);
            return Task.FromResult(partNumber == 2
                ? new HttpResponseSnapshot(403, "AccessDenied", Array.Empty<string>())
                : new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(Context(degree: 1), CancellationToken.None));

        Assert.Equal([1, 2], attempted);
        Assert.Contains("part 2/3", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }
}
