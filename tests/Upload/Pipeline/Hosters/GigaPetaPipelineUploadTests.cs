// <copyright file="GigaPetaPipelineUploadTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class GigaPetaPipelineUploadTests
{
    // Minimal homepage with the rotating upload-form action + the hidden fields the browser
    // posts. The action host (g25.upload.gigapeta.com) is what the pipeline must POST to.
    private const string HomeHtml = """
        <!DOCTYPE html><html><body>
        <form method="post" action="/"><input name="auth_login" /></form>
        <form id="upload-form" action="http://g25.upload.gigapeta.com:81/disk37" method="post" enctype="multipart/form-data">
          <input type="hidden" name="MAX_FILE_SIZE" value="262144000" />
          <input type="hidden" name="adv_sess" value="" />
          <input type="hidden" name="redom" value="gigapeta.com" />
          <input type="file" name="file_0" />
        </form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_HappyPath_PostsToScrapedActionWithAnonymousFieldsAndForwardedCookies()
    {
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, new[]
            {
                "auth_token3=abc123; path=/; domain=.gigapeta.com; httponly",
                "upload_type=single; path=/",
            }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            // The handler keeps 3xx un-followed, so success arrives as a 302 + Location.
            new HttpResponseSnapshot(302, "http://gigapeta.com/dl/11440935a032289", Array.Empty<string>(), "http://gigapeta.com/dl/11440935a032289?done"),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // Download link is the Location with the ?done query stripped.
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("http://gigapeta.com/dl/11440935a032289", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(home);
        Assert.Empty(uploads);

        UploadCall call = Assert.Single(calls);
        Assert.StartsWith("http://g25.upload.gigapeta.com:81/disk37?X-Progress-ID=", call.Endpoint, StringComparison.Ordinal);
        Assert.Equal("262144000", call.ExtraFields["MAX_FILE_SIZE"]);
        Assert.Equal(string.Empty, call.ExtraFields["adv_sess"]); // anonymous → empty
        Assert.Equal("gigapeta.com", call.ExtraFields["redom"]);

        Assert.NotNull(call.Headers);
        Assert.Equal("http://gigapeta.com", call.Headers!["Origin"]);
        Assert.Equal("http://gigapeta.com/", call.Headers["Referer"]);
        // Homepage Set-Cookies are echoed back to the :81 upload host (same .gigapeta.com domain).
        Assert.Contains("auth_token3=abc123", call.Headers["Cookie"], StringComparison.Ordinal);
        Assert.Contains("upload_type=single", call.Headers["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoLocationHeader_FallsBackToDownloadLinkInBody()
    {
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, "<html><body>Done: http://gigapeta.com/dl/bodyOnlyLink123 </body></html>", Array.Empty<string>(), null),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("http://gigapeta.com/dl/bodyOnlyLink123", tc.FileUrl);
    }

    [Fact]
    public async Task RunAsync_FileExceedsAnonymousCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        // Nothing queued — the pre-check must fail before the homepage GET or the upload.
        GigaPetaPipeline pipeline = MakePipeline(new(), new(), out List<UploadCall> calls);

        AttemptContext ctx = MakeContext(fileSize: 262144000L + 1);
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("GigaPeta", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RunAsync_HomepageWithoutUploadForm_YieldsAttemptFailedWithoutUpload()
    {
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, "<html><body>no upload form here</body></html>", Array.Empty<string>()),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads: new(), out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached the upload step
    }

    [Fact]
    public async Task RunAsync_UploadReturnsNoDownloadLink_YieldsAttemptFailed_WithoutRetry()
    {
        // A 200 with no link is a parse-shape failure, not a transient verdict — it must fail
        // terminally on the FIRST response (the 403/429/5xx class is what retries).
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, "<html><body>no link anywhere</body></html>", Array.Empty<string>(), null),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("did not return a download link", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Single(calls); // no retry for a non-transient verdict
    }

    [Fact]
    public async Task RunAsync_Upload403_RetriesAgainstFreshlyScrapedNode_ThenSucceeds()
    {
        // The nodes 403 while the client's serialization slot is held (or a node is unhealthy). The
        // retry must NOT reuse the old node: it re-scrapes the homepage and posts to the fresh
        // assignment (here g7/disk7, proving the second scrape was used).
        const string Home2 = """
            <form id="upload-form" action="http://g7.upload.gigapeta.com:81/disk7" method="post">
              <input type="hidden" name="MAX_FILE_SIZE" value="262144000" />
            </form>
            """;
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            new HttpResponseSnapshot(200, Home2, Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(403, "<html><head><title>403 Forbidden</title></head><body><center>nginx/1.2.3</center></body></html>", Array.Empty<string>(), null),
            new HttpResponseSnapshot(302, string.Empty, Array.Empty<string>(), "http://gigapeta.com/dl/retriedOk?done"),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("http://gigapeta.com/dl/retriedOk", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("http://g25.upload.gigapeta.com:81/disk37?", calls[0].Endpoint, StringComparison.Ordinal);
        Assert.StartsWith("http://g7.upload.gigapeta.com:81/disk7?", calls[1].Endpoint, StringComparison.Ordinal); // fresh node
    }

    [Fact]
    public async Task RunAsync_UploadPersistent403_FailsAfterBoundedRetries()
    {
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(403, "403 Forbidden", Array.Empty<string>(), null),
            new HttpResponseSnapshot(403, "403 Forbidden", Array.Empty<string>(), null),
            new HttpResponseSnapshot(403, "403 Forbidden", Array.Empty<string>(), null),
            new HttpResponseSnapshot(403, "403 Forbidden", Array.Empty<string>(), null),
        });
        GigaPetaPipeline pipeline = MakePipeline(home, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("403", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Equal(4, calls.Count); // the initial send + 3 bounded retries, then terminal
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // The pipeline no longer catches the upload's transport fault into a terminal
        // AttemptFailed/AttemptCancelled — the shared retry layer (AttemptRunner) owns that. A
        // body-incomplete mid-send reset must PROPAGATE out of RunAsync so AttemptRunner can classify
        // it (body-not-fully-sent → re-run the whole pipeline against a fresh scraped node).
        Queue<HttpResponseSnapshot> home = new(new[]
        {
            new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
        });
        int uploadCalls = 0;
        GigaPetaPipeline pipeline = new(
            getSnapshotOverride: _ => home.Dequeue(),
            uploadOverride: (filePath, endpoint, extraFields, headers, speed) =>
            {
                uploadCalls++;
                throw new HttpRequestException(
                    "Error while copying content to a stream",
                    new UploadBodyTransferException(
                        new IOException("Unable to write data to the transport connection", new SocketException(10054))));
            });

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex)); // the safe-to-retry signal survives intact
        Assert.Equal(1, uploadCalls); // single-shot; no in-pipeline retry
    }

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }
        return events;
    }

    private static GigaPetaPipeline MakePipeline(
        Queue<HttpResponseSnapshot> home,
        Queue<HttpResponseSnapshot> uploads,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new GigaPetaPipeline(
            getSnapshotOverride: _ => home.Dequeue(),
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                captured.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(extraFields),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });
    }

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);

    // Anonymous context: a blank login DTO (no username), exactly what the wizard builds for
    // an anonymous-capable hoster with no selected account.
    private static AttemptContext MakeContext(long fileSize = 1_048_576L) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\1mb.bin",
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "GigaPeta",
        Credentials = new FileHosterLoginDto { FileHosterName = "GigaPeta" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
