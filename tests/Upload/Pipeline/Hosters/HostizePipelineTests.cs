// <copyright file="HostizePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Text.Json;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Hostize — anonymous, keyless, presigned S3 multipart. Fixtures are the real replies its API gave
/// (2026-08-09), verified by uploading at one part and at three, both of which the share page then
/// listed at the right size. What's pinned hardest is that the ticket's own numbers are obeyed: the
/// part size comes from the response, never from a constant.
/// </summary>
public class HostizePipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    /// <summary>A real ticket, trimmed. Three parts of 5 MiB.</summary>
    private const string TicketJson = """
        {"id":"Pf6M_0gNjc","tickets":[{"partSize":5242880,"uploadId":"2~9dC7a7twiRP_k2UdS5TZBhvigstFt98","partUrls":[{"partNumber":1,"url":"https://s3.dynabic.com/hostize/a.rar?X-Amz-Signature=one"},{"partNumber":2,"url":"https://s3.dynabic.com/hostize/a.rar?X-Amz-Signature=two"},{"partNumber":3,"url":"https://s3.dynabic.com/hostize/a.rar?X-Amz-Signature=three"}]}]}
        """;

    private const string CompletedJson = """
        {"id":"Pf6M_0gNjc","expiresAt":"2026-08-09T16:05:49.437Z","size":"12000000","downloads":0,"uploaded":true}
        """;

    public HostizePipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_AsksForATicket_PutsEveryPart_ThenPublishes()
    {
        List<string> posts = [];
        List<int> parts = [];

        HostizePipeline pipeline = new(
            (url, _) =>
            {
                posts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(
                    url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                    url.EndsWith("/request", StringComparison.Ordinal) ? TicketJson : CompletedJson,
                    Array.Empty<string>()));
            },
            (_, partNumber, _) =>
            {
                parts.Add(partNumber);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag\""));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(12_000_000), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.hostize.com/api/upload/request", posts[0]);
        Assert.Equal([1, 2, 3], parts);
        Assert.Equal("https://www.hostize.com/api/upload/complete", posts[1]);
        Assert.Equal("https://www.hostize.com/s/Pf6M_0gNjc", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_SlicesByTheTicketsOwnPartSize_NotAConstant()
    {
        // storage.to and VikingFile both taught this: the documented part size and the live one differ,
        // and a mis-sliced multipart corrupts every file over one part.
        List<long> lengths = [];
        HostizePipeline pipeline = MakePipeline([], lengths);

        await DrainAsync(pipeline.RunAsync(MakeContext(12_000_000), CancellationToken.None));

        Assert.Equal([5_242_880, 5_242_880, 1_514_240], lengths);
        Assert.Equal(12_000_000, lengths.Sum());
    }

    [Theory]
    // No parts at all, or a zero part size, would slice the file into nothing.
    [InlineData("""{"id":"abc","tickets":[{"partSize":0,"partUrls":[{"partNumber":1,"url":"https://x/1"}]}]}""")]
    [InlineData("""{"id":"abc","tickets":[{"partSize":5242880,"partUrls":[]}]}""")]
    [InlineData("""{"id":"abc","tickets":[]}""")]
    [InlineData("""{"tickets":[{"partSize":5242880,"partUrls":[{"partNumber":1,"url":"https://x/1"}]}]}""")]
    [InlineData("<html>nope</html>")]
    public void ParseTicket_RejectsATicketItCannotUpload(string body)
    {
        (HostizePipeline.UploadTicket? ticket, string? error) =
            HostizePipeline.ParseTicket(new HttpResponseSnapshot(201, body, Array.Empty<string>()));

        Assert.Null(ticket);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseTicket_ReadsTheShareIdPartSizeAndUrls()
    {
        (HostizePipeline.UploadTicket? ticket, string? error) =
            HostizePipeline.ParseTicket(new HttpResponseSnapshot(201, TicketJson, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("Pf6M_0gNjc", ticket!.Value.ShareId);
        Assert.Equal(5_242_880, ticket.Value.PartSize);
        Assert.Equal(3, ticket.Value.PartUrls.Count);
    }

    [Fact]
    public async Task RunAsync_ARefusedPart_StopsBeforeSendingTheRestOrPublishing()
    {
        // On a 20 GB host, carrying on past a refused part is the difference between a quick error
        // and hours of wasted transfer — and publishing afterwards would share a truncated file.
        List<string> posts = [];
        List<int> parts = [];

        HostizePipeline pipeline = new(
            (url, _) =>
            {
                posts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(201, TicketJson, Array.Empty<string>()));
            },
            (_, partNumber, _) =>
            {
                parts.Add(partNumber);
                return Task.FromResult(partNumber == 2
                    ? new HttpResponseSnapshot(403, "AccessDenied", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(12_000_000), CancellationToken.None));

        Assert.Equal([1, 2], parts);
        Assert.Single(posts);   // request only — complete never ran
        Assert.Contains("part 2/3", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AShareThatIsNeverCompleted_IsNotAnUpload()
    {
        HostizePipeline pipeline = new(
            (url, _) => Task.FromResult(url.EndsWith("/complete", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(500, """{"message":"boom"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(201, TicketJson, Array.Empty<string>())),
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(12_000_000), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_DeclaresTheFilesRealNameAndSize()
    {
        // The ticket is issued against them: a wrong size yields the wrong number of part URLs.
        List<string> bodies = [];
        HostizePipeline pipeline = new(
            (url, json) =>
            {
                bodies.Add(json);
                return Task.FromResult(new HttpResponseSnapshot(
                    url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                    url.EndsWith("/request", StringComparison.Ordinal) ? TicketJson : CompletedJson,
                    Array.Empty<string>()));
            },
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext(12_000_000), CancellationToken.None));

        JsonElement declared = JsonDocument.Parse(bodies[0]).RootElement.GetProperty("files")[0];
        Assert.Equal("probe.rar", declared.GetProperty("name").GetString());
        Assert.Equal(12_000_000, declared.GetProperty("size").GetInt64());

        // Complete carries only the share id — this host finalises the multipart itself, so unlike
        // storage.to and VikingFile there are no ETags to send.
        Assert.Equal("Pf6M_0gNjc", JsonDocument.Parse(bodies[1]).RootElement.GetProperty("shareId").GetString());
    }

    [Theory]
    [InlineData(CompletedJson, "2026-08-09T16:05:49.437Z")]
    [InlineData("""{"id":"x"}""", null)]
    [InlineData("not json", null)]
    public void ReadExpiry_SurfacesThe24HourLife(string body, string? expected)
        => Assert.Equal(expected, HostizePipeline.ReadExpiry(body));

    [Fact]
    public async Task RunAsync_RefusesAFileOverTheFreePlansCap_BeforeAskingForATicket()
    {
        List<string> posts = [];
        HostizePipeline pipeline = MakePipeline(posts, []);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext((20L * 1000 * 1000 * 1000) + 1), CancellationToken.None));

        Assert.Empty(posts);
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public void Hostize_IsAnonymousOnly_AtTheFreePlansCap()
    {
        HostizePipeline pipeline = new();
        Assert.Equal("Hostize", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Uploading with an account needs a Pro subscription's key, so no account is offered.
        Assert.False(pipeline.SupportsAccounts);

        Assert.Equal(20L * 1000 * 1000 * 1000, pipeline.MaxFileSize);
        Assert.Equal("www.hostize.com", FileHosterClient.FileHosters["Hostize"]);
    }

    private static HostizePipeline MakePipeline(List<string> posts, List<long> lengths) => new(
        (url, _) =>
        {
            posts.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(
                url.EndsWith("/request", StringComparison.Ordinal) ? 201 : 200,
                url.EndsWith("/request", StringComparison.Ordinal) ? TicketJson : CompletedJson,
                Array.Empty<string>()));
        },
        (_, _, length) =>
        {
            lengths.Add(length);
            return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "Hostize",
        Credentials = new FileHosterLoginDto { FileHosterName = "Hostize", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }
}
