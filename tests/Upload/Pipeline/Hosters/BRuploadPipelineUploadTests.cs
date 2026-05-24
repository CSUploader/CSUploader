// <copyright file="BRuploadPipelineUploadTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class BRuploadPipelineUploadTests
{
    private const string LoginHtml = """
        <!DOCTYPE html><html><body>
        <form method="POST" action="/" name="FL">
          <input type="hidden" name="op" value="login">
          <input type="hidden" name="token" value="abc123csrf">
          <input type="hidden" name="rand" value="">
          <input type="hidden" name="redirect" value="">
          <input type="text" name="login" value="">
          <input type="password" name="password">
          <input type="submit" value="Enviar">
        </form>
        </body></html>
        """;

    // Upload form HTML — the action URL deliberately points at server54.brupload.net to
    // confirm the pipeline uses the URL parsed from the form rather than the main www
    // host (the real failure mode reported by users uploading multi-GB files).
    private const string UploadFormHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" method="POST" enctype="multipart/form-data" action="https://server54.brupload.net/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="formSess77">
          <input type="hidden" name="utype" value="reg">
          <input type="file" name="file_0">
          <input type="submit" name="upload" value="Start upload">
        </form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_HappyPath_PostsToScrapedActionUrlWithExtendedFields()
    {
        Queue<string> gets = new(new[] { LoginHtml, UploadFormHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[]
            {
                "login=eomeu29998; Path=/",
                "xfss=cookieXfss; Path=/",
            }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"xyz789","file_status":"OK"}]""", Array.Empty<string>()),
        });

        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out List<UploadCall> uploadCalls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://www.brupload.net/xyz789", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(gets);
        Assert.Empty(postForms);
        Assert.Empty(uploads);

        UploadCall call = Assert.Single(uploadCalls);
        // Must POST to the scraped per-user upload host, not the main www host.
        Assert.Equal("https://server54.brupload.net/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        // sess_id should be the value scraped from the form, NOT the xfss cookie value.
        Assert.Equal("formSess77", call.ExtraFields["sess_id"]);

        // Exactly mirror what the BRupload upload form posts via the browser (verified
        // against a Fiddler capture of a successful browser upload): the file form's
        // hidden inputs (sess_id, utype, file_descr, file_public) + the advanced_opts
        // table inputs (link_rcpt, link_pass, to_folder, all empty) + the upload submit
        // value + keepalive=1 appended by formToXHR. Omitting file_public/file_descr/
        // upload causes fs.cgi to 500 with "failed while requesting fs.cgi" because
        // fs.cgi reads file_public to register the file's visibility.
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal("1", call.ExtraFields["keepalive"]);
        Assert.Equal(string.Empty, call.ExtraFields["file_descr"]);
        Assert.Equal("1", call.ExtraFields["file_public"]);
        Assert.Equal(string.Empty, call.ExtraFields["link_rcpt"]);
        Assert.Equal(string.Empty, call.ExtraFields["link_pass"]);
        Assert.Equal(string.Empty, call.ExtraFields["to_folder"]);
        Assert.Equal("Start upload", call.ExtraFields["upload"]);
    }

    [Fact]
    public async Task RunAsync_UploadFormMissingAction_YieldsAuthFailed()
    {
        Queue<string> gets = new(new[] { LoginHtml, "<html>no form</html>" });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=ok; Path=/" }),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads: new(), out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
    }

    [Fact]
    public async Task RunAsync_UploadFormWithoutSessId_FallsBackToXfssCookieValue()
    {
        // No <input name="sess_id"> in the form — pipeline should fall back to the cookie.
        const string formWithoutSessId = """
            <form id="uploadfile" method="POST" enctype="multipart/form-data" action="https://srv.brupload.net/cgi-bin/upload.cgi">
              <input type="file" name="file_0">
            </form>
            """;
        Queue<string> gets = new(new[] { LoginHtml, formWithoutSessId });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=fallbackXfss; Path=/" }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out List<UploadCall> uploadCalls);

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        UploadCall call = Assert.Single(uploadCalls);
        Assert.Equal("fallbackXfss", call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_LoginCsrfTokenMissing_YieldsAuthFailed()
    {
        Queue<string> gets = new(new[] { "<html><body>no form here</body></html>" });
        BRuploadPipeline pipeline = MakePipeline(gets,
            postForms: new(),
            uploads: new(),
            out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_LoginReturnsNoXfssCookie_YieldsAuthFailed()
    {
        Queue<string> gets = new(new[] { LoginHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(200, "<html><body>Wrong password</body></html>", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads: new(), out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsUnauthorized_DropsCachedAuthAndYieldsAuthFailed()
    {
        Queue<string> gets = new(new[] { LoginHtml, UploadFormHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=stale; Path=/" }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"","file_status":"Unauthorized"}]""", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out _);

        List<UploadEvent> first = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Contains(first, e => e is AuthFailed);
        Assert.Contains(first, e => e is AttemptFailed);

        // Cache should be invalidated → second attempt re-runs the full auth sequence.
        gets.Enqueue(LoginHtml);
        gets.Enqueue(UploadFormHtml);
        postForms.Enqueue(new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=fresh; Path=/" }));
        uploads.Enqueue(new HttpResponseSnapshot(200, """[{"file_code":"good","file_status":"OK"}]""", Array.Empty<string>()));

        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        TransferCompleted tc = Assert.Single(second.OfType<TransferCompleted>());
        Assert.Equal("https://www.brupload.net/good", tc.FileUrl);
        Assert.Contains(second, e => e is AuthStarted);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsUnknownStatus_YieldsAttemptFailed()
    {
        Queue<string> gets = new(new[] { LoginHtml, UploadFormHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=ok; Path=/" }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"","file_status":"DiskFull"}]""", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("DiskFull", fail.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SecondAttemptReusesCachedSession_SkipsAuthAndUploadForm()
    {
        Queue<string> gets = new(new[] { LoginHtml, UploadFormHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=cached; Path=/" }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"first","file_status":"OK"}]""", Array.Empty<string>()),
            new HttpResponseSnapshot(200, """[{"file_code":"second","file_status":"OK"}]""", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out _);

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.DoesNotContain(second, e => e is AuthStarted);
        Assert.DoesNotContain(second, e => e is AuthSucceeded);
        TransferCompleted tc = Assert.Single(second.OfType<TransferCompleted>());
        Assert.Equal("https://www.brupload.net/second", tc.FileUrl);
        Assert.Empty(gets);
        Assert.Empty(postForms);
        Assert.Empty(uploads);
    }

    [Fact]
    public async Task RunAsync_FileExceedsMaxFileSize_YieldsAttemptFailedWithoutAnyHttp()
    {
        // No HTTP responses queued — the pre-check must fail before any network call.
        Queue<string> gets = new();
        Queue<HttpResponseSnapshot> postForms = new();
        Queue<HttpResponseSnapshot> uploads = new();
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out _);

        // Force the file size past the declared 1 GiB cap.
        AttemptContext ctx = MakeContextWithSize(2L * 1024 * 1024 * 1024);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("BRupload", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is AuthStarted);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public void Properties_DeclareBRuploadFreeTierLimits()
    {
        BRuploadPipeline pipeline = new();
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal(30, pipeline.MaxFilesPerPackage);
    }

    private static AttemptContext MakeContextWithSize(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\big.iso",
        FileName = "big.iso",
        FileSize = size,
        HosterName = "BRupload",
        Credentials = new FileHosterLoginDto { Id = 42, FileHosterName = "BRupload", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    [Fact]
    public async Task RunAsync_UploadResponseNotJsonArray_YieldsAttemptFailed()
    {
        Queue<string> gets = new(new[] { LoginHtml, UploadFormHtml });
        Queue<HttpResponseSnapshot> postForms = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, new[] { "xfss=ok; Path=/" }),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(502, "<html>Bad Gateway</html>", Array.Empty<string>()),
        });
        BRuploadPipeline pipeline = MakePipeline(gets, postForms, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("502", fail.Reason, StringComparison.Ordinal);
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

    private static BRuploadPipeline MakePipeline(
        Queue<string> gets,
        Queue<HttpResponseSnapshot> postForms,
        Queue<HttpResponseSnapshot> uploads,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new BRuploadPipeline(
            getOverride: _ => gets.Dequeue(),
            postFormOverride: (_, _) => postForms.Dequeue(),
            uploadOverride: (filePath, endpoint, extraFields, _) =>
            {
                captured.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extraFields)));
                return Task.FromResult(uploads.Dequeue());
            });
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> ExtraFields);

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "BRupload",
        Credentials = new FileHosterLoginDto { Id = 42, FileHosterName = "BRupload", Username = "eomeu29998", Password = "eomeu29998" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
