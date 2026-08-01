// <copyright file="DropMeFilesPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// DropMeFiles' anonymous path. Fixtures are the real responses from a browser capture of a
/// signed-out upload (2026-08-01). The two things pinned hardest are the resumable headers — without
/// them the node answers 415 to any body — and the file-id pairing between the chunk session and the
/// save call, which is the one mistake that fails SILENTLY: every request succeeds and the drop is
/// then empty.
/// </summary>
public class DropMeFilesPipelineTests
{
    private const string HomepageHtml = """
        <script>var CHUNKSIZE = '4m'; var SERVERID = '1'; var SPEEDDOWNSIZE = '53687091200';</script>
        """;

    private const string CreateOkJson = """{"jsonrpc" : "2.0", "result" : "k3zyA", "id" : 1209600}""";
    private const string SaveOkJson = """{"jsonrpc" : "2.0", "result" : "Saved", "ziped" : "0"}""";
    private const string FinalChunkJson = """{"jsonrpc" : "2.0", "result" : null, "id" : "id"}""";

    [Fact]
    public async Task RunAsync_UploadsAndReturnsTheDropLink()
    {
        List<string> posts = [];
        List<(string Url, IReadOnlyDictionary<string, string> Headers, long Offset, long Length)> chunks = [];

        DropMeFilesPipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, ["PHPSESSID=abc; path=/"])),
            postFormOverride: (url, form, headers) =>
            {
                posts.Add(url);
                if (url.EndsWith("/upload/create", StringComparison.Ordinal))
                {
                    Assert.Equal("PHPSESSID=abc", headers!["Cookie"]);   // the session from the homepage is carried
                    Assert.Equal("3", form["period"]);                    // 14 days, the longest offered
                    Assert.Equal("4096", form["size"]);
                    return Task.FromResult(new HttpResponseSnapshot(200, CreateOkJson, Array.Empty<string>()));
                }

                return Task.FromResult(new HttpResponseSnapshot(200, SaveOkJson, Array.Empty<string>()));
            },
            chunkOverride: (url, headers, offset, length) =>
            {
                chunks.Add((url, new Dictionary<string, string>(headers), offset, length));
                return Task.FromResult(new HttpResponseSnapshot(200, FinalChunkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://dropmefiles.com/k3zyA", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(
            new[] { "https://dropmefiles.com/s1/upload/create", "https://dropmefiles.com/s1/upload/save" },
            posts);

        (string url, IReadOnlyDictionary<string, string> headers, long offset, long length) = Assert.Single(chunks);
        Assert.Equal("https://dropmefiles.com/s1/uploadrmbl?name=clip.avi&chunk=0&chunks=1&updir=k3zyA", url);
        Assert.Equal(0L, offset);
        Assert.Equal(4096L, length);

        // The three headers that ARE the protocol — without them the node 415s whatever the body.
        Assert.Equal("bytes 0-4095/4096", headers["Content-Range"]);
        Assert.StartsWith("k3zyA_o_", headers["Session-ID"], StringComparison.Ordinal);
        Assert.Equal($"attachment; filename=\"{headers["Session-ID"]}\"", headers["Content-Disposition"]);
    }

    [Fact]
    public async Task RunAsync_SaveReusesTheChunkSessionsFileId()
    {
        // THE silent failure. The chunk Session-ID is "<uid>_<fileId>" and save's files[0].id must be
        // that same <fileId> — it is how the server pairs the saved record with the bytes it spooled.
        // Mint a second id for save and everything still "works": chunks 201/200, save answers
        // "Saved", a link comes back — and the drop page then reads "Files were deleted due to
        // unexpected error while uploading". Learned by doing exactly that against the live service.
        string? sessionId = null;
        string? savedFilesJson = null;

        DropMeFilesPipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            postFormOverride: (url, form, _) =>
            {
                if (url.EndsWith("/upload/save", StringComparison.Ordinal))
                {
                    savedFilesJson = form["files"];
                    Assert.Equal("k3zyA", form["uid"]);
                }

                return Task.FromResult(new HttpResponseSnapshot(
                    200, url.EndsWith("/upload/create", StringComparison.Ordinal) ? CreateOkJson : SaveOkJson, Array.Empty<string>()));
            },
            chunkOverride: (_, headers, _, _) =>
            {
                sessionId = headers["Session-ID"];
                return Task.FromResult(new HttpResponseSnapshot(200, FinalChunkJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        using JsonDocument doc = JsonDocument.Parse(savedFilesJson!);
        string savedId = doc.RootElement[0].GetProperty("id").GetString()!;

        Assert.Equal($"k3zyA_{savedId}", sessionId);        // …the pairing the server matches on
        Assert.Equal("k3zyA", doc.RootElement[0].GetProperty("dir").GetString());
        Assert.Equal(4096, doc.RootElement[0].GetProperty("size").GetInt64());
        Assert.Equal(100, doc.RootElement[0].GetProperty("percent").GetInt32());
        Assert.Equal(5, doc.RootElement[0].GetProperty("status").GetInt32());   // plupload DONE
    }

    [Fact]
    public async Task RunAsync_LargeFile_WalksTheRangeAcrossChunks()
    {
        List<string> ranges = [];
        List<string> sessions = [];

        DropMeFilesPipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            postFormOverride: (url, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, url.EndsWith("/upload/create", StringComparison.Ordinal) ? CreateOkJson : SaveOkJson, Array.Empty<string>())),
            chunkOverride: (url, headers, offset, length) =>
            {
                ranges.Add(headers["Content-Range"]);
                sessions.Add(headers["Session-ID"]);
                bool last = offset + length >= 10 * 1024 * 1024;
                return Task.FromResult(last
                    ? new HttpResponseSnapshot(200, FinalChunkJson, Array.Empty<string>())
                    : new HttpResponseSnapshot(201, $"{offset}-{offset + length - 1}/{10 * 1024 * 1024}", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = 10 * 1024 * 1024 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(
            new[] { "bytes 0-4194303/10485760", "bytes 4194304-8388607/10485760", "bytes 8388608-10485759/10485760" },
            ranges);
        Assert.Single(sessions.Distinct()); // one session across every chunk — that is what ties them together
    }

    [Fact]
    public async Task RunAsync_AntiAbuseRefusal_SurfacesTheHostsOwnReason_AndSendsNothing()
    {
        // Its create endpoint answers this after a burst from one address. "Upload failed" would send
        // the user hunting; the host's own word plus what it means is far more use.
        DropMeFilesPipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"jsonrpc" : "2.0", "error" : {"code": 99, "message": "Spam"}, "id" : "id"}""", Array.Empty<string>())),
            chunkOverride: (_, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("Spam", reason, StringComparison.Ordinal);
        Assert.Contains("rapid uploads", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_ChunkRejected_FailsWithoutSaving()
    {
        // 415 is exactly what a missing/wrong resumable header earns, so it must never be reported as
        // success — and save must not run, or the drop would claim a file that isn't there.
        bool saved = false;
        DropMeFilesPipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            postFormOverride: (url, _, _) =>
            {
                if (url.EndsWith("/upload/save", StringComparison.Ordinal))
                {
                    saved = true;
                }

                return Task.FromResult(new HttpResponseSnapshot(200, CreateOkJson, Array.Empty<string>()));
            },
            chunkOverride: (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(415, string.Empty, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("415", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.False(saved);
    }

    [Theory]
    [InlineData("<script>var SERVERID = '1';</script>", "1")]
    [InlineData("var SERVERID = '12'", "12")]
    [InlineData("<html>no server here</html>", null)]
    public void ParseServerId_ReadsThePageGlobal(string html, string? expected)
        => Assert.Equal(expected, DropMeFilesPipeline.ParseServerId(html));

    [Theory]
    [InlineData(CreateOkJson, "k3zyA", null)]
    [InlineData("""{"error":{"code":99,"message":"Spam"}}""", null, "Spam")]
    [InlineData("""{"error":{"code":1,"message":"Overload"}}""", null, "Overload")]
    [InlineData("<html>502</html>", null, "did not return a drop id")]
    public void ParseCreateResponse_ReadsTheUidOrTheRefusal(string json, string? uid, string? errorFragment)
    {
        (string? gotUid, string? gotError) = DropMeFilesPipeline.ParseCreateResponse(json, 200);

        Assert.Equal(uid, gotUid);
        if (errorFragment is null)
        {
            Assert.Null(gotError);
        }
        else
        {
            Assert.Contains(errorFragment, gotError!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(201, "0-4194303/5225142", false, true)]   // mid-upload: 201 + the accumulated range
    [InlineData(200, FinalChunkJson, true, true)]         // last chunk: 200 + JSON
    [InlineData(201, "", true, false)]                    // a 201 on the LAST chunk means it never completed
    [InlineData(415, "", false, false)]                   // the missing-headers answer
    [InlineData(200, """{"error":"too big"}""", true, false)]
    public void ValidateChunkResponse_AcceptsOnlyTheRightAnswerForThePosition(int status, string body, bool isLast, bool expectOk)
    {
        string? error = DropMeFilesPipeline.ValidateChunkResponse(new HttpResponseSnapshot(status, body, Array.Empty<string>()), isLast);
        Assert.Equal(expectOk, error is null);
    }

    [Theory]
    // The site's own BeforeUpload rule, verbatim: archives/executables at or under MAXSCANSIZE (75 MB)
    // take the virus-scan route. A release .rar under that ceiling is a very ordinary case.
    [InlineData("part.rar", 50_000_000, "uploadch")]
    [InlineData("part.rar", 90_000_000, "uploadrmbl")]   // over the scan ceiling
    [InlineData("setup.exe", 1_000, "uploadch")]
    [InlineData("clip.avi", 50_000_000, "uploadrmbl")]   // not a scanned extension
    [InlineData("part.r10", 50_000_000, "uploadrmbl")]   // only "rar" is listed, not the numbered parts
    [InlineData("huge.iso", 60_000_000_000, "uploadsl")]
    public void UploadRouteFor_MatchesTheSitesOwnRule(string name, long size, string expected)
        => Assert.Equal(expected, DropMeFilesPipeline.UploadRouteFor(name, size));

    [Fact]
    public async Task DropMeFiles_IsAnonymous_Serialised_AndDeclaresTheSitesCap()
    {
        DropMeFilesPipeline pipeline = new();
        Assert.Equal("DropMeFiles", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(53_687_091_200L, pipeline.MaxFileSize);  // the site's own "up to 50 Gb"

        // One at a time: each file needs its own drop, and bursts of those earn a "Spam" refusal.
        Assert.Equal(1, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { Id = 0 }));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("DropMeFiles"));
        Assert.Equal("dropmefiles.com", FileHosterClient.FileHosters["DropMeFiles"]);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u", "p", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("no accounts", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 4096,
        HosterName = "DropMeFiles",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "DropMeFiles" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
