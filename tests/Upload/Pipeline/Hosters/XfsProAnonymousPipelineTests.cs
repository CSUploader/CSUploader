// <copyright file="XfsProAnonymousPipelineTests.cs" company="CSUploader">
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
/// The shared xfspro anonymous protocol, exercised through both hosts that use it. Every fixture is
/// a real response: FILEAXA's from a browser capture of an anonymous AND a signed-in upload,
/// DailyUploads' from a live probe. The two hosts answer the finalise in DIFFERENT shapes, which is
/// the main reason this base exists and the main thing these pin.
/// </summary>
public class XfsProAnonymousPipelineTests
{
    private const string NodeOkJson = """{"url":"https://s26.fileaxa.com/cgi-bin"}""";
    private const string ChunkOkJson = """{"status":"OK"}""";

    /// <summary>FILEAXA: a full links object.</summary>
    private const string FinaliseWithLinksJson = """{"file_code":"8boye7dyy1mr","status":"OK","links":{"download_link":"https://fileaxa.com/8boye7dyy1mr","delete_link":"https://fileaxa.com/8boye7dyy1mr?killcode=x"}}""";

    /// <summary>DailyUploads: file_code only, verbatim from the live probe.</summary>
    private const string FinaliseCodeOnlyJson = """{"status":"OK","file_code":"1n9lpl4eakkc"}""";

    [Fact]
    public async Task Fileaxa_UsesTheServersOwnDownloadLink()
    {
        Dictionary<string, string>? fields = null;
        FileaxaPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>())),
            finaliseOverride: (_, f) => { fields = new Dictionary<string, string>(f); return Task.FromResult(new HttpResponseSnapshot(200, FinaliseWithLinksJson, Array.Empty<string>())); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("FILEAXA"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://fileaxa.com/8boye7dyy1mr", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(string.Empty, fields!["sess_id"]);   // the anonymous signal, present and empty
        Assert.Equal(16, fields["sid"].Length);
    }

    [Fact]
    public async Task DailyUploads_BuildsTheLinkFromFileCode_BecauseItSendsNoLinksObject()
    {
        // Verbatim from the live probe: {"status":"OK","file_code":"…"} and nothing else. Reading only
        // links.download_link — as the first version of this code did — would report "no link" for a
        // perfectly successful upload.
        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, """{"url":"https://cdn89.dailyuploads.net/cgi-bin"}""", Array.Empty<string>())),
            chunkOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>())),
            finaliseOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://dailyuploads.net/1n9lpl4eakkc", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task LargeFile_SlicesAt100MiBUnderOneSid()
    {
        // The server appends by SID with no offset or range sent, so every chunk must carry the SAME
        // sid — and nothing exists until the finalise.
        List<(long Offset, long Length)> chunks = [];
        string? sid = null;

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, offset, length) => { chunks.Add((offset, length)); return Task.FromResult(new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>())); },
            finaliseOverride: (_, f) => { sid = f["sid"]; return Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())); });

        const long MiB = 1024 * 1024;
        AttemptContext ctx = MakeContext("DailyUploads") with { FileSize = (250 * MiB) + 7 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(new[] { (0L, 100 * MiB), (100 * MiB, 100 * MiB), (200 * MiB, (50 * MiB) + 7) }, chunks);
        Assert.NotNull(sid);
        Assert.Single(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task ChunkRejected_FailsWithoutFinalising()
    {
        // import_file is the only record-creating step, so a bad chunk must never reach it.
        bool finalised = false;
        FileaxaPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, """{"status":"ERROR"}""", Array.Empty<string>())),
            finaliseOverride: (_, _) => { finalised = true; return Task.FromResult(new HttpResponseSnapshot(200, FinaliseWithLinksJson, Array.Empty<string>())); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("FILEAXA"), CancellationToken.None));

        Assert.Contains("rejected chunk 0", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(finalised);
    }

    [Fact]
    public async Task DeadNode_ResendsAgainstAFreshOne()
    {
        // /server rotates and some nodes are simply down: measured on DailyUploads, where dn12
        // answered every PUT with a 500 while cdn89 and cdn183 took the same bytes. A single bad draw
        // must not fail the file.
        Queue<string> nodes = new(["""{"url":"https://dn12.dailyuploads.net/cgi-bin"}""", """{"url":"https://cdn89.dailyuploads.net/cgi-bin"}"""]);
        List<string> chunkUrls = [];
        List<string> sids = [];

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, nodes.Dequeue(), Array.Empty<string>())),
            chunkOverride: (url, _, _) =>
            {
                chunkUrls.Add(url);
                return Task.FromResult(url.Contains("dn12", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(500, "<html>Internal Server Error</html>", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (_, f) => { sids.Add(f["sid"]); return Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://dailyuploads.net/1n9lpl4eakkc", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(2, chunkUrls.Count);
        Assert.Contains("dn12", chunkUrls[0], StringComparison.Ordinal);
        Assert.Contains("cdn89", chunkUrls[1], StringComparison.Ordinal);   // …a DIFFERENT node
        Assert.Single(events.OfType<TransferStarted>());                    // one transfer to the user
    }

    [Fact]
    public async Task DeadNodeOfferedAgain_IsRedrawnPastInsteadOfSurrenderedTo()
    {
        // The reported failure. /server draws from a small pool — twelve consecutive live lookups
        // returned four distinct nodes — so the retry's lookup lands back on the SAME dead node
        // roughly one time in six. This used to give up right there ("no different node to move to"),
        // turning a recoverable draw into a failed file. The lookup must step past it instead.
        Queue<string> nodes = new([
            """{"url":"https://dn12.dailyuploads.net/cgi-bin"}""",
            """{"url":"https://dn12.dailyuploads.net/cgi-bin"}""",   // the retry is offered it AGAIN…
            """{"url":"https://cdn89.dailyuploads.net/cgi-bin"}""",  // …and must keep drawing to here
        ]);
        List<string> chunkUrls = [];

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, nodes.Dequeue(), Array.Empty<string>())),
            chunkOverride: (url, _, _) =>
            {
                chunkUrls.Add(url);
                return Task.FromResult(url.Contains("dn12", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(500, "<html>Internal Server Error</html>", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://dailyuploads.net/1n9lpl4eakkc", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // And the second offer of dn12 cost NO bytes — it was skipped at the lookup, not sent to.
        Assert.Equal(2, chunkUrls.Count);
        Assert.Contains("cdn89", chunkUrls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANodeThatFailed_IsSkippedForTheRestOfTheBatch()
    {
        // A dead node stays dead — measured, not assumed: dn12 answered three of three PUTs with 500
        // while three sibling nodes each took the same megabyte three of three. These pipelines are
        // registered as singletons, so the file that discovers a bad node can spare every later file
        // from ever sending it a byte. Without this, each file in a batch re-discovers it the
        // expensive way, mid-transfer.
        Queue<string> nodes = new([
            """{"url":"https://dn12.dailyuploads.net/cgi-bin"}""",   // file 1 draws the dead node…
            """{"url":"https://cdn89.dailyuploads.net/cgi-bin"}""",  // …and recovers onto a good one
            """{"url":"https://dn12.dailyuploads.net/cgi-bin"}""",   // file 2 is offered it again…
            """{"url":"https://cdn181.dailyuploads.net/cgi-bin"}""", // …and must never touch it
        ]);
        List<string> chunkUrls = [];

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, nodes.Dequeue(), Array.Empty<string>())),
            chunkOverride: (url, _, _) =>
            {
                chunkUrls.Add(url);
                return Task.FromResult(url.Contains("dn12", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(500, "<html>Internal Server Error</html>", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));
        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Empty(second.OfType<AttemptFailed>());
        Assert.Single(second.OfType<TransferCompleted>());

        // Three sends in total, and only the FIRST file ever reached dn12.
        Assert.Equal(3, chunkUrls.Count);
        Assert.Single(chunkUrls, u => u.Contains("dn12", StringComparison.Ordinal));
        Assert.Contains("cdn181", chunkUrls[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryNodeOfferedIsOneThatFailed_StillSendsRatherThanGivingUpOnOurOwnBookkeeping()
    {
        // The failure record is a cooldown, not a verdict: the host does fix nodes, and a pool this
        // small could otherwise talk the client out of uploading at all.
        List<string> chunkUrls = [];
        int chunkCalls = 0;

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, """{"url":"https://dn12.dailyuploads.net/cgi-bin"}""", Array.Empty<string>())),
            chunkOverride: (url, _, _) =>
            {
                chunkUrls.Add(url);
                return Task.FromResult(++chunkCalls == 1
                    ? new HttpResponseSnapshot(500, "boom", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>()));
            },
            finaliseOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, FinaliseCodeOnlyJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, chunkUrls.Count);   // it sent to the only node on offer a second time
    }

    [Fact]
    public async Task DeadNodes_StopAfterAFixedNumberOfSends()
    {
        // Each retry re-sends the WHOLE file under a fresh SID, so this must never become a loop —
        // that cost is the reason the ceiling is low rather than generous.
        int chunkCalls = 0;
        Queue<string> nodes = new(["""{"url":"https://dn12.a/cgi-bin"}""", """{"url":"https://dn13.a/cgi-bin"}"""]);

        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, nodes.Count > 0 ? nodes.Dequeue() : """{"url":"https://dn14.a/cgi-bin"}""", Array.Empty<string>())),
            chunkOverride: (_, _, _) => { chunkCalls++; return Task.FromResult(new HttpResponseSnapshot(500, "boom", Array.Empty<string>())); },
            finaliseOverride: (_, _) => throw new InvalidOperationException("must not finalise"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Contains("500", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Equal(3, chunkCalls); // the original send + two retries, then it stops
    }

    [Fact]
    public async Task RefusedFile_IsNotRetried()
    {
        // The counterpart guard: a non-5xx refusal is a verdict on the FILE, and re-sending it would
        // only earn the same answer at the cost of the whole transfer.
        int chunkCalls = 0;
        DailyUploadsPipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, NodeOkJson, Array.Empty<string>())),
            chunkOverride: (_, _, _) => { chunkCalls++; return Task.FromResult(new HttpResponseSnapshot(200, """{"status":"ERROR"}""", Array.Empty<string>())); },
            finaliseOverride: (_, _) => throw new InvalidOperationException("must not finalise"));

        await DrainAsync(pipeline.RunAsync(MakeContext("DailyUploads"), CancellationToken.None));

        Assert.Equal(1, chunkCalls);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(200, false)]
    [InlineData(403, false)]
    public void IsNodeUnavailable_MatchesServerFaultsOnly(int status, bool expected)
        => Assert.Equal(expected, XfsProAnonymousPipeline.IsNodeUnavailable(new HttpResponseSnapshot(status, string.Empty, Array.Empty<string>())));

    [Theory]
    [InlineData(NodeOkJson, "https://s26.fileaxa.com/cgi-bin", null)]
    [InlineData("""{"url":"https://s26.fileaxa.com/cgi-bin/"}""", "https://s26.fileaxa.com/cgi-bin", null)]
    [InlineData("""{"nope":1}""", null, "no upload node")]
    [InlineData("<html>502</html>", null, "no upload node")]
    public void ParseNodeResponse_ReadsTheCgiBase(string json, string? expected, string? errorFragment)
    {
        (string? cgi, string? error) = XfsProAnonymousPipeline.ParseNodeResponse(json, 200, "TestHost");

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
    [InlineData(FinaliseWithLinksJson, "https://fileaxa.com/8boye7dyy1mr", null)]   // links preferred…
    [InlineData(FinaliseCodeOnlyJson, "https://h.test/1n9lpl4eakkc", null)]        // …else built from file_code
    [InlineData("""{"error":"unallowed extension"}""", null, "unallowed extension")]
    [InlineData("""{"status":"OK"}""", null, "no link")]
    [InlineData("not json", null, "unreadable")]
    public void ParseFinaliseResponse_HandlesBothReplyShapes(string body, string? url, string? errorFragment)
    {
        (string? got, string? error) = XfsProAnonymousPipeline.ParseFinaliseResponse(
            new HttpResponseSnapshot(200, body, Array.Empty<string>()), "https://h.test", "TestHost");

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
    public void BuildFinaliseFields_MatchesTheCapturedFieldSet()
    {
        Dictionary<string, string> fields = XfsProAnonymousPipeline.BuildFinaliseFields("1234567890123456", "x.rar", sessionId: string.Empty);

        Assert.Equal(
            new[] { "op", "sid", "fname", "sess_id", "file_descr", "file_public", "link_rcpt", "link_pass", "to_folder" }.Order(),
            fields.Keys.Order());
        Assert.Equal("import_file", fields["op"]);
        Assert.Equal("0", fields["file_public"]);
    }

    [Fact]
    public void ParseDeleteLink_IsOptional_BecauseNotEveryHostSendsOne()
    {
        Assert.Equal("https://fileaxa.com/8boye7dyy1mr?killcode=x", XfsProAnonymousPipeline.ParseDeleteLink(FinaliseWithLinksJson));
        Assert.Null(XfsProAnonymousPipeline.ParseDeleteLink(FinaliseCodeOnlyJson)); // DailyUploads sends none
        Assert.Null(XfsProAnonymousPipeline.ParseDeleteLink("not json"));
    }

    [Theory]
    [InlineData("FILEAXA", "fileaxa.com")]
    [InlineData("DailyUploads", "dailyuploads.net")]
    public async Task BothHosts_AreAnonymous_WithNoInheritedCap(string name, string domain)
    {
        IFileHosterPipeline pipeline = name == "FILEAXA" ? new FileaxaPipeline() : new DailyUploadsPipeline();

        Assert.Equal(name, pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Null, NOT XFileSharingApiPipeline's 1 GiB default — these do not derive from it, and a
        // guessed cap would skip files at queue time that the server would have taken.
        Assert.Null(pipeline.MaxFileSize);

        Assert.True(FileHosterClient.FileHosters.ContainsKey(name));
        Assert.Equal(domain, FileHosterClient.FileHosters[name]);
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode(name)); // not the API-key dialog

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

    private static AttemptContext MakeContext(string hoster) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\release.r00",
        FileName = "release.r00",
        FileSize = 4096,
        HosterName = hoster,
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = hoster },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };
}
