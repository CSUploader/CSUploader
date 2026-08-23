// <copyright file="SendspacePipelineTests.cs" company="CSUploader">
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
/// Sendspace's anonymous path. The fixtures are trimmed from the live homepage and from a real
/// upload's result page (2026-08-01). What's pinned hardest is that the whole ticket is scraped per
/// upload — it is single-use and rotates — and that the page's own declared cap is believed over
/// the constant.
/// </summary>
public class SendspacePipelineTests
{
    private const string NodeAction = "https://fs03u.sendspace.com/upload?SPEED_LIMIT=0&MAX_FILE_SIZE=314572800&UPLOAD_IDENTIFIER=1535523552.1785567006.7193ED5B.18.0&DESTINATION_DIR=12&signature=0e569e2dededa041d50760b728886ead";

    /// <summary>Trimmed from the live homepage. The ampersands arrive HTML-escaped, which is the
    /// detail that turns a working action URL into a 404 if it isn't decoded.</summary>
    private const string HomepageHtml = """
        <script>
        var upload_form_max_upload_size = 314572800;
        var upload_form_drag_url = 'https://fs03u.sendspace.com/dragupload?DESTINATION_DIR=12&signature=deadbeef';
        var upload_form_destination_dir = '12';
        </script>
        <form aria-label="Upload files" method="post" action="https://fs03u.sendspace.com/upload?SPEED_LIMIT=0&amp;MAX_FILE_SIZE=314572800&amp;UPLOAD_IDENTIFIER=1535523552.1785567006.7193ED5B.18.0&amp;DESTINATION_DIR=12&amp;signature=0e569e2dededa041d50760b728886ead" enctype="multipart/form-data">
          <input type="hidden" name="PROGRESS_URL" value="https://fs03u.sendspace.com/progress.php?UPLOAD_IDENTIFIER=1535523552.1785567006.7193ED5B.18.0" />
          <input type="hidden" name="js_enabled" id="js_enabled" value="0" />
          <input type="hidden" name="signature" value="0e569e2dededa041d50760b728886ead" />
          <input type="hidden" name="upload_files" id="upload_files" value="" />
          <input type="hidden" name="terms" value="1" id="terms" />
          <input type="file" id="upload_file" name="upload_file[]" class="file" multiple />
        </form>
        """;

    /// <summary>The result page is the upload's own reply — 200, not a redirect.</summary>
    private const string ResultPageHtml = """
        <html><body>
        <input value="https://www.sendspace.com/file/fqpliq2h" />
        <a href="https://www.sendspace.com/delete/fqpliq2h/9e981314ffd56a6ff8572c19656a9475">Delete this file</a>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_ScrapesTheTicketThenPostsTheSitesOwnForm()
    {
        List<string> gets = [];
        List<UploadCall> uploads = [];

        SendspacePipeline pipeline = new(
            getOverride: (url, _) =>
            {
                gets.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>()));
            },
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields), headers));
                return Task.FromResult(new HttpResponseSnapshot(200, ResultPageHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.sendspace.com/file/fqpliq2h", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal("https://www.sendspace.com/", Assert.Single(gets));

        UploadCall call = Assert.Single(uploads);
        Assert.Equal(NodeAction, call.Endpoint); // &amp; decoded — an escaped URL would miss the node
        Assert.Equal("0e569e2dededa041d50760b728886ead", call.Fields["signature"]);
        Assert.Equal("1", call.Fields["js_enabled"]);   // the browser posts 1 even via Browse, not the page's 0
        Assert.Equal("1", call.Fields["terms"]);
        Assert.Equal(string.Empty, call.Fields["file[]"]);
        Assert.Equal(string.Empty, call.Fields["upload_files"]);
        Assert.StartsWith("https://fs03u.sendspace.com/progress.php?", call.Fields["PROGRESS_URL"], StringComparison.Ordinal);

        // The browser sends the node no cookies at all — the signature is the whole authorisation.
        Assert.NotNull(call.Headers);
        Assert.False(call.Headers!.ContainsKey("Cookie"));
        Assert.Equal("https://www.sendspace.com", call.Headers["Origin"]);
    }

    [Fact]
    public async Task RunAsync_ReScrapesTheTicketForEveryUpload()
    {
        // The ticket is single-use and every part of it rotates per page load, so caching one across
        // a batch would post a spent signature.
        int gets = 0;
        SendspacePipeline pipeline = new(
            getOverride: (_, _) =>
            {
                gets++;
                return Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, ResultPageHtml, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(3, gets);
    }

    [Fact]
    public async Task RunAsync_OversizeFile_FailsBeforeSendingAnything()
    {
        // The page declares the cap it will enforce. Believing it turns a doomed 400 MB transfer into
        // an instant refusal carrying the host's own number.
        SendspacePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with { FileSize = 400L * 1024 * 1024 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        // The site advertises "300MB" but its own number is 314572800 = 300 MiB exactly, so the
        // refusal says 300 MiB rather than the 314.57 MB a decimal rendering would produce.
        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("300 MiB", reason, StringComparison.Ordinal);
        Assert.Contains("400 MiB", reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_UploadRefused_SurfacesTheErrorCodeFromTheRedirect()
    {
        // The node's only diagnostic is a redirect to its error page; there is no body to read.
        SendspacePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                301, string.Empty, Array.Empty<string>(), "https://www.sendspace.com/uploadprocerr.html?e=0")));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("error 0", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    /// <summary>Verbatim from a live batch, 2026-08-01: fs03u answered a release part with nginx's
    /// own error page while other files in the same batch went through.</summary>
    private const string NginxUnavailableHtml = """
        <html>
        <head><title>503 Service Temporarily Unavailable</title></head>
        <body><center><h1>503 Service Temporarily Unavailable</h1></center><hr><center>nginx</center></body>
        </html>
        <!-- a padding to disable MSIE and Chrome friendly error page -->
        """;

    [Fact]
    public async Task RunAsync_NodeIsDown_RetriesOnceAgainstAFreshlyScrapedNode()
    {
        // The homepage assigns a rotating node, so re-scraping is what moves the retry off the one
        // that is out. Without this a single sick node fails a file that the next one would take.
        List<string> endpoints = [];
        int gets = 0, uploads = 0;

        SendspacePipeline pipeline = new(
            getOverride: (_, _) =>
            {
                gets++;
                string html = gets == 1
                    ? HomepageHtml
                    : HomepageHtml.Replace("fs03u", "fs12u", StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseSnapshot(200, html, Array.Empty<string>()));
            },
            uploadOverride: (_, endpoint, _, _, _) =>
            {
                endpoints.Add(endpoint);
                uploads++;
                return Task.FromResult(uploads == 1
                    ? new HttpResponseSnapshot(503, NginxUnavailableHtml, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, ResultPageHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://www.sendspace.com/file/fqpliq2h", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, gets);      // re-scraped…
        Assert.Equal(2, uploads);   // …and re-sent once
        Assert.StartsWith("https://fs03u.", endpoints[0], StringComparison.Ordinal);
        Assert.StartsWith("https://fs12u.", endpoints[1], StringComparison.Ordinal); // a DIFFERENT node

        // One transfer as far as the UI is concerned.
        Assert.Single(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_NodesKeepFailing_StopsAfterExactlyOneRetry()
    {
        // The retry re-sends the whole file, so it must never become a loop.
        int uploads = 0;
        SendspacePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(503, NginxUnavailableHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("node is unavailable", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("503", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("padding to disable", reason, StringComparison.Ordinal); // not 500 bytes of nginx filler
        Assert.Equal(2, uploads); // the original send + exactly one retry
    }

    [Fact]
    public async Task RunAsync_FileRefused_IsNeverReUploaded()
    {
        // The counterpart guard: /uploadprocerr.html is a verdict on the FILE (prohibited type, file
        // in use). Re-sending only earns it again, at the cost of the whole transfer.
        int uploads = 0, gets = 0;
        SendspacePipeline pipeline = new(
            getOverride: (_, _) => { gets++; return Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(
                    301, string.Empty, Array.Empty<string>(), "https://www.sendspace.com/uploadprocerr.html?e=0"));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(1, uploads); // sent once…
        Assert.Equal(1, gets);    // …and no second ticket was even fetched
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(502, true)]
    [InlineData(500, true)]
    [InlineData(504, true)]
    [InlineData(301, false)]  // the error-page redirect is a verdict on the file
    [InlineData(200, false)]
    [InlineData(403, false)]  // not a node fault — do not burn the file on it twice
    public void IsNodeUnavailable_MatchesGatewayFaultsOnly(int status, bool expected)
        => Assert.Equal(expected, SendspacePipeline.IsNodeUnavailable(new HttpResponseSnapshot(status, string.Empty, Array.Empty<string>())));

    [Fact]
    public async Task RunAsync_HomepageWithoutAForm_FailsWithoutUploading()
    {
        SendspacePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(503, "<html>maintenance</html>", Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("no upload form", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public void ParseHomepage_ReadsTheTicketAndDecodesTheAction()
    {
        (SendspacePipeline.UploadTicket? ticket, string? error) = SendspacePipeline.ParseHomepage(HomepageHtml, 200);

        Assert.Null(error);
        Assert.Equal(NodeAction, ticket!.Value.ActionUrl);
        Assert.DoesNotContain("&amp;", ticket.Value.ActionUrl, StringComparison.Ordinal);
        Assert.Equal("0e569e2dededa041d50760b728886ead", ticket.Value.Signature);
        Assert.Equal(314572800L, ticket.Value.MaxFileSize);

        // The drag-upload URL sits in the same page with its own signature — it must not be picked up
        // in place of the form action, which is the endpoint a browser actually posts to.
        Assert.Contains("/upload?", ticket.Value.ActionUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("dragupload", ticket.Value.ActionUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseHomepage_WithoutASignature_IsNotUsable()
    {
        (SendspacePipeline.UploadTicket? ticket, string? error) = SendspacePipeline.ParseHomepage(
            """<form aria-label="Upload files" action="https://fs03u.sendspace.com/upload?x=1"></form>""", 200);

        Assert.Null(ticket);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseUploadResponse_TakesTheLinkOffTheResultPage()
    {
        (string? url, string? error) = SendspacePipeline.ParseUploadResponse(
            new HttpResponseSnapshot(200, ResultPageHtml, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("https://www.sendspace.com/file/fqpliq2h", url);
    }

    [Fact]
    public void ParseDeleteLink_FindsTheOnlyWayAnAnonymousUploadCanBeRemoved()
        => Assert.Equal(
            "https://www.sendspace.com/delete/fqpliq2h/9e981314ffd56a6ff8572c19656a9475",
            SendspacePipeline.ParseDeleteLink(ResultPageHtml));

    [Fact]
    public async Task RunAsync_LogsTheDeleteLink_BecauseItAppearsExactlyOnce()
    {
        // It is on the upload's own reply and nowhere else — not on the file's page, and an
        // anonymous upload has no account behind it. Dropping it means the upload can never be
        // taken down, so it goes to the log where the user can find it.
        List<string> logged = [];
        Mock<IAppLogger> logger = new();
        logger.Setup(l => l.Log(It.IsAny<object?>(), It.IsAny<LogType>(), It.IsAny<string>(), It.IsAny<HttpTransaction?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<object?, LogType, string, HttpTransaction?, string, string, int>((_, _, msg, _, _, _, _) => logged.Add(msg));

        SendspacePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, HomepageHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, ResultPageHtml, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext() with { Logger = logger.Object }, CancellationToken.None));

        Assert.Contains(logged, m => m.Contains("/delete/fqpliq2h/9e981314ffd56a6ff8572c19656a9475", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sendspace_IsAnonymous_AndDeclaresTheSitesOwnCap()
    {
        SendspacePipeline pipeline = new();
        Assert.Equal("Sendspace", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(314572800L, pipeline.MaxFileSize); // upload_form_max_upload_size, i.e. 300 MB exactly

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Sendspace"));
        Assert.Equal("www.sendspace.com", FileHosterClient.FileHosters["Sendspace"]);

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
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 4096,
        HosterName = "Sendspace",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "Sendspace" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> Fields,
        IReadOnlyDictionary<string, string>? Headers);
}
