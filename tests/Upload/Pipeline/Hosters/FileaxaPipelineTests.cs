// <copyright file="FileaxaPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// FILEAXA on the xfspro chunked plugin. Every fixture is verbatim from a browser capture of BOTH an
/// anonymous and a signed-in upload (2026-08-02) — the two differ only in <c>sess_id</c>, which is
/// what makes the anonymous path shippable.
/// </summary>
public class FileaxaPipelineTests
{
    private const string NodeOkJson = """{"url":"https://s26.fileaxa.com/cgi-bin"}""";
    private const string ChunkOkJson = """{"status":"OK"}""";
    private const string FinaliseOkJson = """{"file_code":"8boye7dyy1mr","status":"OK","links":{"download_link":"https://fileaxa.com/8boye7dyy1mr","delete_link":"https://fileaxa.com/8boye7dyy1mr?killcode=x"}}""";

    [Fact]
    public async Task RunAsync_AnonymousUpload_SendsAnEmptySessIdAndReturnsTheServersLink()
    {
        List<string> gets = [];
        List<(string Url, long Offset, long Length)> chunks = [];
        Dictionary<string, string>? finaliseFields = null;
        string? finaliseUrl = null;

        FileaxaPipeline pipeline = new(
            getOverride: url => { gets.Add(url); return Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())); },
            chunkOverride: (url, offset, length) =>
            {
                chunks.Add((url, offset, length));
                return Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (url, fields) =>
            {
                finaliseUrl = url;
                finaliseFields = new Dictionary<string, string>(fields);
                return Task.FromResult(new HttpResponseSnapshot(200, FinaliseOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://fileaxa.com/8boye7dyy1mr", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal("https://fileaxa.com/server", Assert.Single(gets));

        (string chunkUrl, long offset, long length) = Assert.Single(chunks);
        Assert.Equal("https://s26.fileaxa.com/cgi-bin/put_chunk.cgi", chunkUrl);
        Assert.Equal(0L, offset);
        Assert.Equal(4096L, length);

        Assert.Equal("https://s26.fileaxa.com/cgi-bin/api.cgi", finaliseUrl);
        Assert.Equal("import_file", finaliseFields!["op"]);
        Assert.Equal("release.r00", finaliseFields["fname"]);
        Assert.Equal(string.Empty, finaliseFields["sess_id"]); // …the anonymous signal, and it must be PRESENT
        Assert.True(finaliseFields.ContainsKey("sess_id"));
        Assert.Equal(16, finaliseFields["sid"].Length);        // the client-minted 16-digit upload id
    }

    [Fact]
    public async Task RunAsync_LargeFile_SlicesAt100MiBUnderOneSid()
    {
        // The server appends by SID with no offset or range sent, so every chunk of a file must carry
        // the SAME sid — and the link only exists once import_file runs.
        List<(long Offset, long Length)> chunks = [];
        string? sid = null;

        FileaxaPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, offset, length) =>
            {
                chunks.Add((offset, length));
                return Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (_, fields) =>
            {
                sid = fields["sid"];
                return Task.FromResult(new HttpResponseSnapshot(200, FinaliseOkJson, Array.Empty<string>()));
            });

        const long MiB = 1024 * 1024;
        AttemptContext ctx = MakeContext() with { FileSize = (250 * MiB) + 7 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(
            new[] { (0L, 100 * MiB), (100 * MiB, 100 * MiB), (200 * MiB, (50 * MiB) + 7) },
            chunks);
        Assert.NotNull(sid);
        Assert.Single(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_ChunkRejected_FailsWithoutFinalising()
    {
        // import_file is the only record-creating step, so a bad chunk must never reach it — otherwise
        // the host would be asked to assemble a file it doesn't have.
        bool finalised = false;
        FileaxaPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, """{"status":"ERROR"}""", Array.Empty<string>())),
            finaliseOverride: (_, _) => { finalised = true; return Task.FromResult(new HttpResponseSnapshot(200, FinaliseOkJson, Array.Empty<string>())); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("rejected chunk 0", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(finalised);
    }

    [Theory]
    [InlineData(NodeOkJson, "https://s26.fileaxa.com/cgi-bin", null)]
    [InlineData("""{"url":"https://s26.fileaxa.com/cgi-bin/"}""", "https://s26.fileaxa.com/cgi-bin", null)] // trailing slash trimmed
    [InlineData("""{"nope":1}""", null, "no upload node")]
    [InlineData("<html>502</html>", null, "no upload node")]
    public void ParseNodeResponse_ReadsTheCgiBase(string json, string? expected, string? errorFragment)
    {
        (string? cgi, string? error) = FileaxaPipeline.ParseNodeResponse(json, 200);

        Assert.Equal(expected, cgi);
        if (errorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(errorFragment, error!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(FinaliseOkJson, "https://fileaxa.com/8boye7dyy1mr", null)]
    [InlineData("""{"error":"unallowed extension"}""", null, "unallowed extension")]
    [InlineData("""{"status":"OK"}""", null, "no link")]     // OK without links is not usable
    [InlineData("not json", null, "unreadable")]
    public void ParseFinaliseResponse_PrefersTheServersOwnLink(string body, string? url, string? errorFragment)
    {
        (string? got, string? error) = FileaxaPipeline.ParseFinaliseResponse(new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Equal(url, got);
        if (errorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(errorFragment, error!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ParseDeleteLink_FindsTheOnlyWayAnAnonymousUploadCanBeRemoved()
    {
        // It is on the finalise reply and nowhere else, and an anonymous upload has no account behind
        // it — so the pipeline logs it rather than dropping it (as Sendspace does).
        Assert.Equal("https://fileaxa.com/8boye7dyy1mr?killcode=x", FileaxaPipeline.ParseDeleteLink(FinaliseOkJson));
        Assert.Null(FileaxaPipeline.ParseDeleteLink("""{"status":"OK"}"""));
        Assert.Null(FileaxaPipeline.ParseDeleteLink("not json"));
    }

    [Fact]
    public void BuildFinaliseFields_MatchesTheCapturedFieldSet()
    {
        // Verbatim from the capture, including the four trailing empties the site's own JS sends.
        Dictionary<string, string> fields = FileaxaPipeline.BuildFinaliseFields("1234567890123456", "x.rar", sessionId: string.Empty);

        Assert.Equal(
            new[] { "op", "sid", "fname", "sess_id", "file_descr", "file_public", "link_rcpt", "link_pass", "to_folder" }.Order(),
            fields.Keys.Order());
        Assert.Equal("0", fields["file_public"]);
    }

    [Fact]
    public async Task Fileaxa_IsAnonymous_AndSaysSoWhenAnAccountIsAdded()
    {
        FileaxaPipeline pipeline = new();
        Assert.Equal("FILEAXA", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Null(pipeline.MaxFileSize); // nothing publishes a figure this code can read

        Assert.True(FileHosterClient.FileHosters.ContainsKey("FILEAXA"));
        Assert.Equal("fileaxa.com", FileHosterClient.FileHosters["FILEAXA"]);

        // NOT the API-key sign-in dialog: it was briefly wired that way while this was believed to be
        // an account-only REST host, and a sign-in button that leads nowhere is worse than none.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("FILEAXA"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u", "p", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("Anonymous", result.Message, StringComparison.OrdinalIgnoreCase);
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
        FilePath = @"C:\nope\release.r00",
        FileName = "release.r00",
        FileSize = 4096,
        HosterName = "FILEAXA",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "FILEAXA" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
