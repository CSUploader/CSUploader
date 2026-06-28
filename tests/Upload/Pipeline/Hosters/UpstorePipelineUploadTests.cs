// <copyright file="UpstorePipelineUploadTests.cs" company="CSUploader">
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
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class UpstorePipelineUploadTests
{
    // Minimal homepage with the rotating Dropzone upload-form action plus the page's login form
    // (whose action must NOT be matched — the scrape is anchored on "newupload").
    private const string HomeHtml = """
        <!DOCTYPE html><html><body>
        <form action="/account/login/" method="post"><input name="login" /></form>
        <form action="https://d128.upstore.net/newupload/" class="dropzone" id="fileloader" enctype="multipart/form-data" method="post"></form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_HappyPath_PostsFileToScrapedActionAndReturnsUpstoreLink()
    {
        Queue<HttpResponseSnapshot> home = new(new[] { new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()) });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            // Real upload-response shape from the 2026-06-28 capture.
            new HttpResponseSnapshot(200, """{"hash":"w5oRyyt","name":"x.bin","uname":"x.bin"}""", Array.Empty<string>(), null),
        });
        UpstorePipeline pipeline = MakePipeline(home, uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://upstore.net/w5oRyyt", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(home);
        Assert.Empty(uploads);

        UploadCall call = Assert.Single(calls);
        // POSTed to the scraped rotating node verbatim (no extra query), with no hidden fields.
        Assert.Equal("https://d128.upstore.net/newupload/", call.Endpoint);
        Assert.Empty(call.ExtraFields);
        Assert.NotNull(call.Headers);
        Assert.Equal("https://upstore.net", call.Headers!["Origin"]);
        Assert.Equal("https://upstore.net/", call.Headers["Referer"]);
        Assert.Equal("XMLHttpRequest", call.Headers["X-Requested-With"]);
        Assert.Equal("application/json", call.Headers["Accept"]);
        Assert.False(call.Headers.ContainsKey("Cookie")); // anonymous — no cookie/token
    }

    [Fact]
    public async Task RunAsync_FileExceedsAnonymousCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        // Nothing queued — the 2 GiB pre-check must fail before the homepage GET or the upload.
        UpstorePipeline pipeline = MakePipeline(new(), new(), out List<UploadCall> calls);

        AttemptContext ctx = MakeContext(fileSize: (2L * 1024 * 1024 * 1024) + 1);
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Upstore", fail.Reason, StringComparison.Ordinal);
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
        UpstorePipeline pipeline = MakePipeline(home, uploads: new(), out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached the upload step
    }

    [Fact]
    public async Task RunAsync_UploadReturnsNon2xx_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> home = new(new[] { new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()) });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(403, "<html><head><title>403 Forbidden</title></head></html>", Array.Empty<string>(), null),
        });
        UpstorePipeline pipeline = MakePipeline(home, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("403", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadResponseHasNoHash_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> home = new(new[] { new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()) });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"name":"x.bin"}""", Array.Empty<string>(), null),
        });
        UpstorePipeline pipeline = MakePipeline(home, uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A body-incomplete mid-send reset must PROPAGATE so AttemptRunner can classify it
        // (body-not-fully-sent → re-run the whole pipeline against a fresh scraped node).
        Queue<HttpResponseSnapshot> home = new(new[] { new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()) });
        int uploadCalls = 0;
        UpstorePipeline pipeline = new(
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

        Assert.True(UploadBodyTransferException.IsInChain(ex));
        Assert.Equal(1, uploadCalls); // single-shot; no in-pipeline retry
    }

    [Fact]
    public async Task RunAsync_Account_LogsInAndPostsFileWithUsid()
    {
        Queue<HttpResponseSnapshot> home = new(new[] { new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()) });
        Queue<HttpResponseSnapshot> uploads = new(new[] { new HttpResponseSnapshot(200, """{"hash":"acctHash"}""", Array.Empty<string>(), null) });
        // Login response sets the usid account credential (Set-Cookie), like the real 302.
        HttpResponseSnapshot loginResp = new(302, string.Empty, new[] { "usid=USID123; path=/; domain=upstore.net; HttpOnly", "upst=sess; path=/" }, "/");

        List<UploadCall> calls = [];
        List<(string Url, IReadOnlyDictionary<string, string> Form)> logins = [];
        UpstorePipeline pipeline = new(
            getSnapshotOverride: _ => home.Dequeue(),
            postFormOverride: (url, form) => { logins.Add((url, new Dictionary<string, string>(form))); return loginResp; },
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extraFields), headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://upstore.net/acctHash", tc.FileUrl);

        // Logged in to the right endpoint with email/password/send.
        (string loginUrl, IReadOnlyDictionary<string, string> form) = Assert.Single(logins);
        Assert.Equal("https://upstore.net/account/login/", loginUrl);
        Assert.Equal("u@example.com", form["email"]);
        Assert.Equal("pw", form["password"]);
        Assert.Equal("Login", form["send"]);

        // The upload carried the usid captured from the login Set-Cookie.
        UploadCall call = Assert.Single(calls);
        Assert.Equal("USID123", call.ExtraFields["usid"]);
    }

    [Fact]
    public async Task RunAsync_Account_LoginFails_YieldsAttemptFailedWithoutUpload()
    {
        // Wrong credentials: the login re-renders the page (200) with NO usid cookie.
        List<UploadCall> calls = [];
        UpstorePipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            postFormOverride: (_, _) => new HttpResponseSnapshot(200, "<html>login page</html>", Array.Empty<string>(), null),
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extraFields), headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(200, """{"hash":"x"}""", Array.Empty<string>(), null));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached the upload
    }

    [Fact]
    public async Task CheckAccountAsync_ValidLogin_ReturnsValidWithEmail()
    {
        UpstorePipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, new[] { "usid=USID123; path=/; domain=upstore.net" }, "/"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "pw", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("u@example.com", result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccountAsync_WrongCredentials_ReturnsInvalid()
    {
        UpstorePipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()),
            postFormOverride: (_, _) => new HttpResponseSnapshot(200, "<html>login page</html>", Array.Empty<string>(), null),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "wrong", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }
        return events;
    }

    private static UpstorePipeline MakePipeline(
        Queue<HttpResponseSnapshot> home,
        Queue<HttpResponseSnapshot> uploads,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new UpstorePipeline(
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

    private static AttemptContext MakeContext(long fileSize = 1_048_576L) =>
        MakeContextWith(new FileHosterLoginDto { FileHosterName = "Upstore", IsAnonymous = true }, fileSize);

    private static AttemptContext MakeAccountContext(long fileSize = 1_048_576L) =>
        MakeContextWith(
            new FileHosterLoginDto { Id = 51, FileHosterName = "Upstore", Username = "u@example.com", Password = "pw", IsAnonymous = false },
            fileSize);

    private static AttemptContext MakeContextWith(FileHosterLoginDto credentials, long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\1mb.bin",
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "Upstore",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
