// <copyright file="UploadEePipelineTests.cs" company="CSUploader">
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
/// upload.ee — the tree's only Uber-Uploader host. Fixtures are the real responses from a browser
/// capture and a live run (2026-08-05). Two things are pinned above all: that the upload id comes
/// from the SERVER (inventing one dies inside their Perl), and that BOTH redirect shapes are handled
/// — the browser is answered with a 302, this client with a 200 carrying a JS redirect.
/// </summary>
public class UploadEePipelineTests
{
    private const string IdJs = """if(typeof startUpload==='function'){startUpload("c93cc90ca1aeac83b3586aad022b9b62",0);}""";

    private const string FinishedHtml = """
        <html><body>
        <h1 class="pageTitle">File successfully uploaded!!</h1><br />
        View file:<br /><a href="https://www.upload.ee/files/19619815/csu-probe.rar.html">https://www.upload.ee/files/19619815/csu-probe.rar.html</a><br /><br />
        Delete file:<br /><a href="https://www.upload.ee/files/19619815/csu-probe.rar.html?killcode=43942159516900476294">delete</a>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_UsesTheServersUploadId_AndReturnsTheViewLink()
    {
        List<string> gets = [];
        string? uploadUrl = null;

        UploadEePipeline pipeline = new(
            getOverride: url =>
            {
                gets.Add(url);
                return Task.FromResult(url.Contains("ubr_link_upload", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));
            },
            uploadOverride: (_, url, fields, _, _) =>
            {
                uploadUrl = url;
                Assert.Empty(fields); // the capture's POST carries the file and nothing else
                return Task.FromResult(new HttpResponseSnapshot(
                    302, string.Empty, Array.Empty<string>(),
                    "https://www.upload.ee/?page=finished&upload_id=c93cc90ca1aeac83b3586aad022b9b62"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The id must be the one the SERVER handed back — a self-invented one reaches ubr_upload.pl
        // and dies there ("could not open link file"), because the .link file is written when the
        // server issues an id.
        Assert.NotNull(uploadUrl);
        Assert.Contains("X-Progress-ID=c93cc90ca1aeac83b3586aad022b9b62", uploadUrl!, StringComparison.Ordinal);
        Assert.Contains("upload_id=c93cc90ca1aeac83b3586aad022b9b62", uploadUrl!, StringComparison.Ordinal);

        // Step 1 first, result page last.
        Assert.Contains("ubr_link_upload.php?rnd_id=", gets[0], StringComparison.Ordinal);
        Assert.Contains("page=finished", gets[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JsRedirect_IsFollowedJustLikeThe302()
    {
        // The capture (a browser) got a 302 with a Location. This client gets 200 and
        // parent.location.href='…' instead — the iframe-era redirect. Relying on either alone would
        // pass in testing and fail in the field, so both are handled.
        UploadEePipeline pipeline = new(
            getOverride: url => Task.FromResult(url.Contains("ubr_link_upload", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())
                : new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                "<html><body>UPLOAD.EE<script>parent.location.href='https://www.upload.ee/?page=finished&upload_id=c93cc90ca1aeac83b3586aad022b9b62';</script></body></html>",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenTheHandlerAlreadyFollowedTheRedirect_UsesTheBodyItHasRatherThanRefetching()
    {
        // THE SHAPE THIS APP ACTUALLY SEES, and the one the first implementation got wrong: our
        // HttpHandler follows the 302, so the upload's own response IS the finished page — 200, no
        // Location, no JS redirect. The first version discarded that, invented a ?page=finished URL and
        // re-fetched it, which upload.ee answers with its HOMEPAGE once the id has been consumed. Every
        // unit test passed and the live run failed, because none of them modelled this client.
        List<string> gets = [];
        UploadEePipeline pipeline = new(
            getOverride: url =>
            {
                gets.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, IdJs, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // Only the id request — the result page was already in hand, so no second round trip.
        Assert.Single(gets);
        Assert.Contains("ubr_link_upload.php", gets[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoUploadId_FailsWithoutSendingAnything()
    {
        // If the id step doesn't answer with one, uploading would only earn the Perl error — so don't.
        bool uploaded = false;
        UploadEePipeline pipeline = new(
            getOverride: _ => Task.FromResult(new HttpResponseSnapshot(200, "/* nothing useful */", Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("upload id", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(uploaded);
    }

    [Theory]
    [InlineData(IdJs, "c93cc90ca1aeac83b3586aad022b9b62")]
    [InlineData("""if(typeof startUpload==='function'){startUpload("ABC123def456",0);}""", "ABC123def456")]
    [InlineData("/* no call at all */", null)]
    public void ParseUploadId_ReadsTheIdOutOfTheJavaScript(string body, string? expected)
    {
        (string? id, string? error) = UploadEePipeline.ParseUploadId(new HttpResponseSnapshot(200, body, Array.Empty<string>()));
        Assert.Equal(expected, id);
        Assert.Equal(expected is null, error is not null);
    }

    [Fact]
    public void ParseFinishedPage_ReadsTheLinkAndTheKillcode()
    {
        (string? url, string? delete, string? error) =
            UploadEePipeline.ParseFinishedPage(new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html", url);

        // The killcode shows once, on this page, and an anonymous upload has no account to manage the
        // file from — so it gets logged rather than dropped.
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html?killcode=43942159516900476294", delete);
    }

    [Fact]
    public async Task RunAsync_OversizedFile_RejectedBeforeAnyTransfer()
    {
        bool touched = false;
        UploadEePipeline pipeline = new(
            getOverride: _ => { touched = true; return Task.FromResult(new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())); },
            uploadOverride: (_, _, _, _, _) => { touched = true; return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext() with { FileSize = (100L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(touched);
    }

    [Fact]
    public void UploadEe_IsAnonymous_AndRegistered()
    {
        UploadEePipeline pipeline = new();
        Assert.Equal("Upload.ee", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal("upload.ee", FileHosterClient.FileHosters["Upload.ee"]);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\csu-probe.rar",
        FileName = "csu-probe.rar",
        FileSize = 1024,
        HosterName = "Upload.ee",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "Upload.ee", IsAnonymous = true },
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
