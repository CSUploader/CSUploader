// <copyright file="FileMiragePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
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
/// FileMirage — anonymous, chunked. Fixtures are the real bodies its node and its upload endpoint
/// returned (2026-08-08), verified by uploading a 4 MB file (one chunk) and a 101 MiB file (two), both
/// of which the site then served back at their full size.
/// </summary>
public class FileMiragePipelineTests : IDisposable
{
    /// <summary>A real file on disk: the pipeline opens one before the chunk loop, so the stubbed
    /// chunks still need something to open. Its length is irrelevant — the chunking is driven by
    /// <see cref="AttemptContext.FileSize"/>, which is what lets a two-line test cover a 300 MB split.</summary>
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string ServersJson =
        """{"success":true,"data":{"server":"https://store1.filemirage.com","upload_id":"msk3g645d865"}}""";

    private const string DoneJson =
        """{"success":true,"data":{"url":"https://filemirage.com/file/4pkeqbqw"}}""";

    /// <summary>What a chunk that isn't the last one answers: a success with no url yet.</summary>
    private const string PendingJson = """{"success":true,"data":{"uploaded":true}}""";

    public FileMiragePipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_AsksForANode_ThenSendsOneChunkCarryingItsOwnFieldSet()
    {
        List<string> gets = [];
        List<string> endpoints = [];
        List<Dictionary<string, string>> fields = [];

        FileMiragePipeline pipeline = MakePipeline(gets, endpoints, fields, _ => DoneJson);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://filemirage.com/api/servers", Assert.Single(gets));

        // The upload goes to the node the host named, never to the site itself.
        Assert.Equal("https://store1.filemirage.com/upload.php", Assert.Single(endpoints));

        Dictionary<string, string> sent = Assert.Single(fields);
        Assert.Equal("probe.rar", sent["filename"]);
        Assert.Equal("0", sent["chunk_number"]);
        Assert.Equal("1", sent["total_chunks"]);

        Assert.Equal("https://filemirage.com/file/4pkeqbqw", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_SplitsAtTheHostsOwn99MbChunkSize_AndNumbersFromZero()
    {
        // Its page declares upload_chunk_size: 99 (MB). A 101 MiB file is therefore two chunks — the
        // split that the live 101 MiB upload exercised, and the one a single-chunk test can't reach.
        // chunk_number is 0-based and total_chunks is on EVERY chunk, which is how the host knows when
        // to assemble; getting either wrong leaves the file in pieces on their side.
        List<Dictionary<string, string>> fields = [];
        const long Size = (99L * 1024 * 1024) + (2L * 1024 * 1024);

        FileMiragePipeline pipeline = MakePipeline([], [], fields, i => i == 0 ? PendingJson : DoneJson);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(Size), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(["0", "1"], fields.Select(f => f["chunk_number"]));
        Assert.All(fields, f => Assert.Equal("2", f["total_chunks"]));

        // All chunks of one file share one id, or the host assembles nothing.
        Assert.Single(fields.Select(f => f["upload_id"]).Distinct());
    }

    [Fact]
    public async Task RunAsync_GivesEveryFileItsOwnUploadId()
    {
        // Its own uploader keys the id on Date.now(), so two files started in the same millisecond
        // share one — and a shared id means the host assembles two files into each other.
        List<Dictionary<string, string>> fields = [];
        FileMiragePipeline pipeline = MakePipeline([], [], fields, _ => DoneJson);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal(2, fields.Count);
        Assert.NotEqual(fields[0]["upload_id"], fields[1]["upload_id"]);
        Assert.All(fields, f => Assert.Matches("^[0-9a-f]{16}$", f["upload_id"]));
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFirstBadChunk_WithoutSendingTheRest()
    {
        // A chunk that fails means the assembled file would be corrupt, so the remaining chunks are
        // wasted transfer — on a 50 GiB host that is the difference between a quick error and hours.
        List<Dictionary<string, string>> fields = [];
        const long Size = (99L * 1024 * 1024) * 3;

        FileMiragePipeline pipeline = new(
            _ => Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>())),
            (_, sent, _) =>
            {
                fields.Add(new Dictionary<string, string>(sent, StringComparer.Ordinal));
                return Task.FromResult(fields.Count == 2
                    ? new HttpResponseSnapshot(500, "nope", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, PendingJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(Size), CancellationToken.None));

        Assert.Equal(2, fields.Count);
        Assert.Contains("chunk 2/3", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServersJson, "https://store1.filemirage.com")]
    [InlineData("""{"success":true,"data":{"server":"https://store1.filemirage.com/"}}""", "https://store1.filemirage.com")]
    [InlineData("""{"success":true,"data":{"upload_id":"x"}}""", null)]   // named no server
    [InlineData("""{"success":false,"message":"no"}""", null)]
    [InlineData("<html>maintenance</html>", null)]
    public void ReadNode_TakesTheServerOrNothing(string body, string? expected)
    {
        // A missing server used to be the interesting case: without this the endpoint becomes
        // "/upload.php" and the file is POSTed at whatever that resolves to.
        Assert.Equal(expected, FileMiragePipeline.ReadNode(body)?.TrimEnd('/'));
    }

    [Fact]
    public void ParseChunkResponse_A200ThatSaysSuccessFalse_IsAFailure()
    {
        // Success-shaped failure: the envelope carries its own flag and a false one rides inside a 200.
        (string? url, string? error) = FileMiragePipeline.ParseChunkResponse(
            new HttpResponseSnapshot(200, """{"success":false,"message":"File type not allowed"}""", Array.Empty<string>()),
            0,
            1);

        Assert.Null(url);
        Assert.Contains("File type not allowed", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseChunkResponse_AnIntermediateChunkHasNoUrl_AndThatIsNotAnError()
    {
        (string? url, string? error) = FileMiragePipeline.ParseChunkResponse(
            new HttpResponseSnapshot(200, PendingJson, Array.Empty<string>()),
            0,
            2);

        Assert.Null(url);
        Assert.Null(error);
    }

    [Fact]
    public async Task RunAsync_RefusesAFileOverTheHostsStatedCap_BeforeSendingAnything()
    {
        List<string> gets = [];
        FileMiragePipeline pipeline = MakePipeline(gets, [], [], _ => DoneJson);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(53_687_091_201), CancellationToken.None));

        Assert.Empty(gets);
        Assert.Contains("50", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FileMirage_IsAnonymousOnly_AtTheCapItsOwnPageDeclares()
    {
        FileMiragePipeline pipeline = new();
        Assert.Equal("FileMirage", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Accounts exist on the site but none of it is verified here, so the host must not appear in
        // the Add Account dialog.
        Assert.False(pipeline.SupportsAccounts);

        Assert.Equal(53_687_091_200, pipeline.MaxFileSize);
        Assert.Equal("filemirage.com", FileHosterClient.FileHosters["FileMirage"]);
    }

    private static FileMiragePipeline MakePipeline(
        List<string> gets,
        List<string> endpoints,
        List<Dictionary<string, string>> fields,
        Func<int, string> chunkBody) => new(
        url =>
        {
            gets.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>()));
        },
        (endpoint, sent, _) =>
        {
            endpoints.Add(endpoint);
            int index = int.Parse(sent["chunk_number"], System.Globalization.CultureInfo.InvariantCulture);
            fields.Add(new Dictionary<string, string>(sent, StringComparer.Ordinal));
            return Task.FromResult(new HttpResponseSnapshot(200, chunkBody(index), Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "FileMirage",
        Credentials = new FileHosterLoginDto { FileHosterName = "FileMirage", IsAnonymous = true },
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
