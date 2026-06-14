// <copyright file="HexloadAnonymousUploadTests.cs" company="CSUploader">
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
/// Anonymous (not-logged-in) upload path, verified against a Fiddler capture of hexload.com
/// 2026-06-13: GET homepage → scrape the <c>id="uploadfile"</c> form's per-session
/// <c>droply.top/cgi-bin/upload.cgi?…utype=anon</c> action → multipart POST (empty sess_id,
/// utype=anon) → <c>[{file_code,file_status}]</c> → link <c>https://hexload.com/&lt;code&gt;</c>.
/// </summary>
public class HexloadAnonymousUploadTests
{
    private const string HomeHtml = """
        <!DOCTYPE html><html><body>
        <form method="post" action="/"><input name="auth_login"></form>
        <form id="uploadfile" action="https://46ev7agtixoi.droply.top/cgi-bin/upload.cgi?upload_type=file&utype=anon">
          <input type="hidden" name="sess_id" value="">
          <input type="hidden" name="utype" value="anon">
          <input type="file" name="file_0">
        </form>
        </body></html>
        """;

    [Fact]
    public void SupportsAnonymousUpload_IsTrue()
    {
        Assert.True(new HexloadPipeline().SupportsAnonymousUpload);
    }

    [Fact]
    public async Task RunAsync_AnonymousCredentials_PostsToScrapedServerWithAnonFieldsAndLinksToFileCode()
    {
        Queue<string> gets = new(new[] { HomeHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"k8re8sxrjrip","file_status":"OK"}]""", Array.Empty<string>()),
        });
        HexloadPipeline pipeline = MakePipeline(gets, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://hexload.com/k8re8sxrjrip", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(gets);
        Assert.Empty(uploads);

        UploadCall call = Assert.Single(calls);
        // Posts to the per-session droply.top server scraped from the form, query intact.
        Assert.Equal("https://46ev7agtixoi.droply.top/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        // Exact anonymous field set from the capture.
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
        Assert.Equal("anon", call.ExtraFields["utype"]);
        Assert.Equal(string.Empty, call.ExtraFields["mode"]);
        Assert.Equal(string.Empty, call.ExtraFields["file_public"]);
        Assert.Equal("1", call.ExtraFields["keepalive"]);

        Assert.NotNull(call.Headers);
        Assert.Equal("https://hexload.com", call.Headers!["Origin"]);
        Assert.Equal("https://hexload.com/", call.Headers["Referer"]);
        Assert.Equal("cross-site", call.Headers["Sec-Fetch-Site"]);
    }

    [Fact]
    public async Task RunAsync_AnonymousButHomepageHasNoUploadForm_YieldsAttemptFailedWithoutUpload()
    {
        Queue<string> gets = new(new[] { "<html><body>maintenance, no upload form</body></html>" });
        HexloadPipeline pipeline = MakePipeline(gets, uploads: new(), out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached the upload step
    }

    [Fact]
    public async Task RunAsync_AnonymousUploadRejected_YieldsAttemptFailed()
    {
        Queue<string> gets = new(new[] { HomeHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"","file_status":"File too big"}]""", Array.Empty<string>()),
        });
        HexloadPipeline pipeline = MakePipeline(gets, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("File too big", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public void MaxFileSizeFor_AnonymousIs2048Mb_AccountKeepsBaseCap()
    {
        HexloadPipeline pipeline = new();
        FileHosterLoginDto anon = new() { IsAnonymous = true };
        FileHosterLoginDto account = new() { Username = "u", Password = "p" };

        // Anonymous per-file cap is 2048 MiB; a logged-in account falls back to the XFS base.
        Assert.Equal(2048L * 1024 * 1024, pipeline.MaxFileSizeFor(anon));
        Assert.Equal(pipeline.MaxFileSize, pipeline.MaxFileSizeFor(account));

        // No per-batch file-count cap — the site's "20 files at once" is a web-form UI limit.
        Assert.Null(pipeline.MaxFilesPerPackage);
    }

    [Fact]
    public async Task RunAsync_AnonymousFileBetween1And2Gib_IsNotSizeRejected()
    {
        Queue<string> gets = new(new[] { HomeHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        HexloadPipeline pipeline = MakePipeline(gets, uploads, out _);

        // 1.5 GiB — over the 1 GiB XFS base cap, under the 2 GiB anonymous cap.
        AttemptContext ctx = MakeAnonymousContext() with { FileSize = 1610612736L };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_AnonymousFileOver2Gib_IsSizeRejectedWithoutHttp()
    {
        HexloadPipeline pipeline = MakePipeline(new(), new(), out List<UploadCall> calls);

        AttemptContext ctx = MakeAnonymousContext() with { FileSize = 2048L * 1024 * 1024 + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Hexload", fail.Reason, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_AnonymousServerUnreachableThenReachable_RetriesWithFreshServerAndSucceeds()
    {
        // The homepage rotates upload servers and can hand out a dead (unresolvable) one. The
        // first POST fails at DNS/connect (no bytes sent); the pipeline must re-fetch the form
        // for a fresh server and retry.
        int getCalls = 0;
        int uploadCalls = 0;
        HexloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { getCalls++; return Task.FromResult(HomeHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                // Real UploadMultipartAsync is async — a DNS/connect failure surfaces as a
                // faulted task, not a synchronous throw. Mock it the same way.
                return uploadCalls == 1
                    ? Task.FromException<HttpResponseSnapshot>(new HttpRequestException(HttpRequestError.NameResolutionError, "simulated DNS failure (WSANO_DATA)"))
                    : Task.FromResult(new HttpResponseSnapshot(200, """[{"file_code":"retried","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://hexload.com/retried", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, uploadCalls);            // retried once
        Assert.Equal(2, getCalls);               // re-scraped a fresh server
        // Only one TransferStarted across the retry — the UI doesn't see a restart.
        Assert.Single(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_AnonymousServerAlwaysUnreachable_FailsAfterRetries()
    {
        int uploadCalls = 0;
        HexloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(HomeHtml),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromException<HttpResponseSnapshot>(new HttpRequestException(HttpRequestError.NameResolutionError, "simulated DNS failure"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("unreachable", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(uploadCalls > 1, $"expected more than one attempt, got {uploadCalls}");
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_AnonymousRetries_FetchDistinctCacheBustedHomepageUrls()
    {
        // The homepage caches the assigned upload server per connection, so retries must use a
        // cache-busting query param to actually get a fresh (different) server each time.
        List<string> getUrls = [];
        int uploadCalls = 0;
        HexloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(HomeHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return uploadCalls < 2
                    ? Task.FromException<HttpResponseSnapshot>(new HttpRequestException(HttpRequestError.NameResolutionError, "dead server"))
                    : Task.FromResult(new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Equal(2, getUrls.Count);
        Assert.All(getUrls, u => Assert.Contains("?_=", u, StringComparison.Ordinal)); // cache-busted
        Assert.NotEqual(getUrls[0], getUrls[1]);                                       // distinct per retry
    }

    [Fact]
    public async Task RunAsync_AccountUpload_KeepsHttpsUploadServerScheme()
    {
        // Regression: Hexload's /api/upload/server returns the upload server over https (e.g.
        // a *.drewimplemnt.top host with a valid cert that 301s http→https mid-body). The old
        // FlashBit-shape downgrade rewrote it to http and the POST reset (SocketException 10054);
        // the pipeline must POST verbatim over https.
        Queue<string> gets = new(new[]
        {
            """{"msg":"OK","status":200,"sess_id":"sess_hx","result":"https://ynll3vo5a4qr.drewimplemnt.top/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"acctCode","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];
        HexloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(gets.Dequeue()),
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                calls.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(fields),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        // Account (API-key) context — NOT anonymous, so it takes the /api/upload/server path.
        AttemptContext ctx = MakeAnonymousContext() with
        {
            Credentials = new FileHosterLoginDto { Id = 5, FileHosterName = "Hexload", ApiKey = "k" },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://hexload.com/acctCode", tc.FileUrl);
        UploadCall call = Assert.Single(calls);
        // The https upload URL the API returned is POSTed verbatim — NOT downgraded to http.
        Assert.Equal("https://ynll3vo5a4qr.drewimplemnt.top/cgi-bin/upload.cgi", call.Endpoint);
    }

    private static HexloadPipeline MakePipeline(
        Queue<string> gets,
        Queue<HttpResponseSnapshot> uploads,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new HexloadPipeline(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(gets.Dequeue()),
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                captured.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(fields),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });
    }

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }
        return events;
    }

    // The wizard's synthetic Anonymous selection: a blank login DTO flagged IsAnonymous.
    private static AttemptContext MakeAnonymousContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_225_142,
        HosterName = "Hexload",
        Credentials = new FileHosterLoginDto { FileHosterName = "Hexload", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
