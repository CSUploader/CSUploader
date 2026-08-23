// <copyright file="WebsharePipelineTests.cs" company="CSUploader">
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
/// Webshare's anonymous path. Every fixture is a real response captured while verifying the host
/// live on 2026-08-01 — the XML node lookup, the node's JSON ident, and a two-chunk upload that
/// reassembled correctly. What's pinned hardest is the ident/offset threading between chunks and
/// the fact that the emitted link is NOT the one the site's own JS builds.
/// </summary>
public class WebsharePipelineTests
{
    private const string NodeXml = """<?xml version="1.0" encoding="UTF-8"?><response><status>OK</status><url>https://upload2.wsup.cz/api/upload/index.php</url><app_version>30</app_version></response>""";
    private const string NodeUrl = "https://upload2.wsup.cz/api/upload/index.php";
    private const string IdentJson = """{"jsonrpc" : "2.0", "result" : null, "id" : "id", "ident" : "V6Ih6GmFnm"}""";

    [Fact]
    public async Task RunAsync_AnonymousUpload_SendsAnEmptyWstAndReturnsTheServerRenderedLink()
    {
        List<string> lookups = [];
        List<UploadCall> uploads = [];

        WebsharePipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                lookups.Add(url);
                Assert.Empty(form); // the node lookup takes no key and no parameters at all
                return Task.FromResult(new HttpResponseSnapshot(200, NodeXml, Array.Empty<string>()));
            },
            uploadOverride: (filePath, endpoint, fields, _) =>
            {
                uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields)));
                return Task.FromResult(new HttpResponseSnapshot(200, IdentJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(
            "https://webshare.cz/file/V6Ih6GmFnm/the-matrix-1999-2160p-r19",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal("https://webshare.cz/api/upload_url/", Assert.Single(lookups));

        UploadCall call = Assert.Single(uploads);
        Assert.Equal(NodeUrl, call.Endpoint);
        Assert.Equal(string.Empty, call.Fields["wst"]);        // the anonymous signal, and it must be PRESENT
        Assert.True(call.Fields.ContainsKey("wst"));
        Assert.Equal("/", call.Fields["folder"]);
        Assert.Equal("0", call.Fields["private"]);
        Assert.Equal("0", call.Fields["adult"]);
        Assert.Equal("4096", call.Fields["total"]);
        Assert.Equal("0", call.Fields["offset"]);
        Assert.Equal("The.Matrix.1999.2160p.r19", call.Fields["name"]);
        Assert.False(call.Fields.ContainsKey("ident"));         // nothing to thread on a single-shot
    }

    [Fact]
    public async Task RunAsync_LargeFile_ThreadsTheIdentAndRunningOffsetAcrossChunks()
    {
        // The first chunk mints the ident; every later one must carry it back plus how far in it
        // starts, which is how the server assembles the file. Getting either wrong produces orphaned
        // fragments rather than an error, so this is the behaviour worth pinning.
        const long GiB = 1L << 30;
        List<(long Offset, long Length, string? Ident)> chunks = [];

        WebsharePipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, NodeXml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("a 2.5 GiB file must not go in one POST"),
            chunkOverride: (endpoint, fields, offset, length) =>
            {
                Assert.Equal(NodeUrl, endpoint);
                Assert.Equal(string.Empty, fields["wst"]);
                Assert.Equal(offset.ToString(System.Globalization.CultureInfo.InvariantCulture), fields["offset"]);
                Assert.Equal((GiB * 2 + 512).ToString(System.Globalization.CultureInfo.InvariantCulture), fields["total"]);
                chunks.Add((offset, length, fields.TryGetValue("ident", out string? id) ? id : null));
                return Task.FromResult(new HttpResponseSnapshot(200, IdentJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = GiB * 2 + 512 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(3, chunks.Count);
        Assert.Equal((0L, GiB, null), chunks[0]);              // first chunk: no ident yet
        Assert.Equal((GiB, GiB, "V6Ih6GmFnm"), chunks[1]);
        Assert.Equal((GiB * 2, 512L, "V6Ih6GmFnm"), chunks[2]); // remainder, not a padded full chunk
    }

    [Fact]
    public async Task RunAsync_ChunkRefused_StopsInsteadOfPushingTheRest()
    {
        const long GiB = 1L << 30;
        int sent = 0;

        WebsharePipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, NodeXml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("must not single-shot"),
            chunkOverride: (_, _, _, _) =>
            {
                sent++;
                return Task.FromResult(sent == 1
                    ? new HttpResponseSnapshot(200, IdentJson, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, """{"error":{"code":1}}""", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = GiB * 5 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("not enough space", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Equal(2, sent); // stopped at the refusal rather than sending all five
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_NodeLookupRefused_FailsWithTheApisOwnWords()
    {
        WebsharePipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """<?xml version="1.0" encoding="UTF-8"?><response><status>FATAL</status><code>UPLOAD_URL_FATAL_1</code><message>Uploading is temporarily disabled.</message></response>""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("temporarily disabled", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    [InlineData(NodeXml, NodeUrl, null)]
    [InlineData("""<response><status>FATAL</status><message>Nope.</message></response>""", null, "Nope.")]
    [InlineData("""<response><status>OK</status></response>""", null, "no upload node")]   // OK without a url is not OK
    [InlineData("<html>bad gateway</html>", null, "no upload node")]
    public void ParseUploadUrlResponse_ReadsTheNodeOrTheRefusal(string xml, string? url, string? errorFragment)
    {
        (string? gotUrl, string? gotError) = WebsharePipeline.ParseUploadUrlResponse(xml, 200);

        Assert.Equal(url, gotUrl);
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
    [InlineData(IdentJson, "V6Ih6GmFnm", null)]
    [InlineData("""{"error":{"code":2}}""", null, "copyrighted")]
    [InlineData("""{"error":{"code":4}}""", null, "identical")]
    [InlineData("""{"error":{"code":99}}""", null, "error code 99")]
    [InlineData("""{"jsonrpc":"2.0","result":null}""", null, "no ident")]
    [InlineData("", null, "unreadable")]
    public void ParseUploadResponse_ReadsTheIdentOrTheHostsOwnReason(string body, string? ident, string? errorFragment)
    {
        (string? gotIdent, string? gotError) = WebsharePipeline.ParseUploadResponse(new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Equal(ident, gotIdent);
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
    [InlineData("csuploader-probe.bin", "csuploader-probe-bin")]
    [InlineData("The.Matrix.1999.2160p.r19", "the-matrix-1999-2160p-r19")]
    [InlineData("Příliš žluťoučký kůň.rar", "prilis-zlutoucky-kun-rar")] // diacritics stripped, as the site does
    [InlineData("  __weird__  name!!.zip  ", "weird-name-zip")]          // runs collapse, ends trimmed
    public void Slugify_MatchesTheSitesOwnRule(string name, string expected)
        => Assert.Equal(expected, WebsharePipeline.Slugify(name));

    [Fact]
    public void BuildFileLink_UsesTheServerRenderedPath_NotTheSpaFragment()
    {
        // The site's own fileLink() emits webshare.cz/#/file/… — fetching that returns the empty app
        // shell, so a link checker or a forum preview sees nothing. The same path without the "#" is
        // server-rendered (verified live). Emitting the site's version would produce links that only
        // work in a browser.
        string link = WebsharePipeline.BuildFileLink("V6Ih6GmFnm", "csuploader-probe.bin");

        Assert.Equal("https://webshare.cz/file/V6Ih6GmFnm/csuploader-probe-bin", link);
        Assert.DoesNotContain("#", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webshare_IsAnonymous_AndSaysSoWhenAnAccountIsAdded()
    {
        WebsharePipeline pipeline = new();
        Assert.Equal("Webshare", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Null(pipeline.MaxFileSize); // no figure that applies to anonymous is published anywhere

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Webshare"));
        Assert.Equal("webshare.cz", FileHosterClient.FileHosters["Webshare"]);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", "pass", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);
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
        FilePath = @"C:\nope\The.Matrix.1999.2160p.r19",
        FileName = "The.Matrix.1999.2160p.r19",
        FileSize = 4096,
        HosterName = "Webshare",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "Webshare" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
