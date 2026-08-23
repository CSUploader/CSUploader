// <copyright file="FilegoPipelineTests.cs" company="CSUploader">
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
/// Filego — anonymous, three calls. Fixtures are the real envelopes its API returned (2026-08-08),
/// verified by uploading a <c>.rar</c> and a <c>.nfo</c> through the shipped pipeline and downloading
/// both back byte-identical. The load-bearing check is the 200-that-means-failure: this API answers
/// <b>HTTP 200 to its own refusals</b>, so anything reading the status code reports success for an
/// upload that never happened.
/// </summary>
public class FilegoPipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string InitOk =
        """{"code":200,"status":"ok","error":"","id":"ug9j1xuh","pw":"5GiDQWVt6oID5zAw8XErvJgDxZ7S1URs"}""";

    private const string PlainOk = """{"code":200,"status":"ok","error":""}""";

    public FilegoPipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_WalksInitThenBytesThenSave_AndLinksTheIssuedId()
    {
        List<string> postUrls = [];
        List<string> puts = [];

        FilegoPipeline pipeline = new(
            (url, _) =>
            {
                postUrls.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, Reply(url), Array.Empty<string>()));
            },
            (url, _) =>
            {
                puts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, PlainOk, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://filego.io/api/upload/init", postUrls[0]);
        Assert.Equal("https://filego.io/api/upload/file/ug9j1xuh/0", Assert.Single(puts));
        Assert.Equal("https://filego.io/api/upload/save", postUrls[1]);
        Assert.Equal("https://filego.io/ug9j1xuh", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_AsksForTheLongestRetentionTheHostAllows()
    {
        // Its slider runs 1-30 days and starts on 7. Sending the default would silently halve the life
        // of every link the app hands out - the same trap tmpfiles, qu.ax and Litterbox set.
        List<IReadOnlyDictionary<string, string>> posts = [];
        FilegoPipeline pipeline = MakePipeline(posts, []);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("30", posts[1]["expire"]);
    }

    [Fact]
    public async Task RunAsync_DeclaresTheFileTheWayItsOwnClientDoes()
    {
        List<IReadOnlyDictionary<string, string>> posts = [];
        FilegoPipeline pipeline = MakePipeline(posts, []);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("probe.rar", posts[0]["name"]);

        // The files array is JSON, with the lowercase keys its API reads.
        JsonElement declared = JsonDocument.Parse(posts[0]["files"]).RootElement[0];
        Assert.Equal("probe.rar", declared.GetProperty("name").GetString());
        Assert.Equal(4096, declared.GetProperty("size").GetInt64());
        Assert.True(declared.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task RunAsync_CarriesTheWriteTokenOnTheBytes()
    {
        // The pw issued by init is what authorises the PUT; the bytes are refused without it.
        List<IReadOnlyDictionary<string, string>> putHeaders = [];
        FilegoPipeline pipeline = new(
            (url, _) => Task.FromResult(new HttpResponseSnapshot(200, Reply(url), Array.Empty<string>())),
            (_, headers) =>
            {
                putHeaders.Add(new Dictionary<string, string>(headers, StringComparer.Ordinal));
                return Task.FromResult(new HttpResponseSnapshot(200, PlainOk, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("5GiDQWVt6oID5zAw8XErvJgDxZ7S1URs", Assert.Single(putHeaders)["X-Filego-Pw"]);
    }

    [Fact]
    public async Task RunAsync_AFailureInsideA200_IsStillAFailure()
    {
        // THE one that matters. Every reply from this API is HTTP 200 and the verdict lives in the
        // body, so trusting the status code hands the user a link to a file that was never accepted.
        FilegoPipeline pipeline = new(
            (url, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                url.EndsWith("/init", StringComparison.Ordinal)
                    ? InitOk
                    : """{"code":200,"status":"error","error":"Upload not found"}""",
                Array.Empty<string>())),
            (_, _) => Task.FromResult(new HttpResponseSnapshot(200, PlainOk, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains("Upload not found", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ARefusedByteTransfer_NeverReachesSave()
    {
        // A failed PUT leaves nothing to commit; saving anyway would mint a link to an empty id.
        List<string> postUrls = [];
        FilegoPipeline pipeline = new(
            (url, _) =>
            {
                postUrls.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, Reply(url), Array.Empty<string>()));
            },
            (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":200,"status":"error","error":"File too large"}""",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("https://filego.io/api/upload/init", Assert.Single(postUrls));   // no save
        Assert.Contains("File too large", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InitOk, "ug9j1xuh", "5GiDQWVt6oID5zAw8XErvJgDxZ7S1URs")]
    // Success-shaped but useless: the PUT would go to /api/upload/file//0 and the link to the site root.
    [InlineData("""{"code":200,"status":"ok","error":"","pw":"x"}""", null, null)]
    [InlineData("""{"code":200,"status":"ok","error":"","id":"abc"}""", null, null)]
    [InlineData("""{"code":200,"status":"error","error":"Too many uploads"}""", null, null)]
    [InlineData("<html>nope</html>", null, null)]
    public void ParseInit_TakesAUsablePairOrNothing(string body, string? id, string? pw)
    {
        (string? gotId, string? gotPw, string? error) =
            FilegoPipeline.ParseInit(new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Equal(id, gotId);
        Assert.Equal(pw, gotPw);
        Assert.Equal(id is null, error is not null);
    }

    [Fact]
    public void ReadEnvelopeError_StillReportsARealTransportFailure()
    {
        // The envelope is the verdict, but a 502 from the edge has no envelope to read.
        string? error = FilegoPipeline.ReadEnvelopeError(
            new HttpResponseSnapshot(502, "<html>Bad Gateway</html>", Array.Empty<string>()), "sending the file");

        Assert.Contains("502", error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RefusesAnOversizedFileBeforeTouchingTheNetwork()
    {
        List<IReadOnlyDictionary<string, string>> posts = [];
        FilegoPipeline pipeline = MakePipeline(posts, []);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(2_147_483_649), CancellationToken.None));

        Assert.Empty(posts);
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public void Filego_IsAnonymousOnly_AtTheCapItsOwnPageEnforces()
    {
        FilegoPipeline pipeline = new();
        Assert.Equal("Filego", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.SupportsAccounts);
        Assert.Equal(2_147_483_648, pipeline.MaxFileSize);
        Assert.Equal("filego.io", FileHosterClient.FileHosters["Filego"]);
    }

    private static string Reply(string url)
        => url.EndsWith("/init", StringComparison.Ordinal) ? InitOk : PlainOk;

    private static FilegoPipeline MakePipeline(
        List<IReadOnlyDictionary<string, string>> posts,
        List<string> puts) => new(
        (url, fields) =>
        {
            posts.Add(new Dictionary<string, string>(fields, StringComparer.Ordinal));
            return Task.FromResult(new HttpResponseSnapshot(200, Reply(url), Array.Empty<string>()));
        },
        (url, _) =>
        {
            puts.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(200, PlainOk, Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "Filego",
        Credentials = new FileHosterLoginDto { FileHosterName = "Filego", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
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
