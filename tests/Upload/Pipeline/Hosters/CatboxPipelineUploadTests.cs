// <copyright file="CatboxPipelineUploadTests.cs" company="CSUploader">
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

/// <summary>
/// Orchestration tests for <see cref="CatboxPipeline"/> — the single anonymous multipart POST to
/// catbox.moe. The multipart upload is stubbed so the event sequence, the fixed endpoint + fields,
/// the plain-URL parse, and the failure/retry branches run without the network. Verified against the
/// 2026-07-03 live anonymous capture (reqtype=fileupload, fileToUpload, plain-URL response).
/// </summary>
public class CatboxPipelineUploadTests
{
    [Fact]
    public void Properties_DeclareCatboxConfig()
    {
        CatboxPipeline pipeline = new();
        Assert.Equal("Catbox", pipeline.Name);
        Assert.Equal(200L * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Catbox"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_PostsToFixedApiAndReturnsCatboxUrl()
    {
        Queue<HttpResponseSnapshot> uploads = new([new HttpResponseSnapshot(200, "https://files.catbox.moe/p0k8cl.avi", [])]);
        CatboxPipeline pipeline = MakePipeline(uploads, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://files.catbox.moe/p0k8cl.avi", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(uploads);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://catbox.moe/user/api.php", call.Endpoint);      // fixed endpoint, no scrape
        Assert.Equal("fileupload", call.ExtraFields["reqtype"]);
        Assert.False(call.ExtraFields.ContainsKey("userhash"));               // anonymous — no userhash
        Assert.NotNull(call.Headers);
        Assert.Equal("https://catbox.moe", call.Headers!["Origin"]);
        Assert.Equal("https://catbox.moe/", call.Headers["Referer"]);
        Assert.Equal("XMLHttpRequest", call.Headers["X-Requested-With"]);
        Assert.False(call.Headers.ContainsKey("Cookie"));                     // cookieless
    }

    [Fact]
    public async Task RunAsync_FileExceedsCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        CatboxPipeline pipeline = MakePipeline(new(), out List<UploadCall> calls);

        AttemptContext ctx = MakeContext(fileSize: (200L * 1024 * 1024) + 1);
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("catbox.moe", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RunAsync_ServerReturnsErrorText_YieldsAttemptFailed()
    {
        // catbox echoes a plain-text reason (HTTP 200) on failure rather than a URL.
        Queue<HttpResponseSnapshot> uploads = new([new HttpResponseSnapshot(200, "Something went wrong. Try again later.", [])]);
        CatboxPipeline pipeline = MakePipeline(uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Something went wrong", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsNon2xx_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> uploads = new([new HttpResponseSnapshot(412, "banned file type", [])]);
        CatboxPipeline pipeline = MakePipeline(uploads, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("412", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A body-incomplete mid-send reset must PROPAGATE so AttemptRunner classifies it
        // (body-not-fully-sent → re-run the whole pipeline) — the file was never created.
        int uploadCalls = 0;
        CatboxPipeline pipeline = new((filePath, endpoint, extraFields, headers, speed) =>
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

    // ===== account (logged-in) uploads: login -> scrape userhash -> upload with it =====

    // The account page (/user/view.php) carries the userhash as a hidden input, as in the live capture.
    private const string ViewHtml = """<input type="hidden" name="userhash" value="38a5e8a1bb24345b8055058c5">""";

    [Fact]
    public async Task RunAsync_Account_LogsInScrapesUserhashAndPostsItWithTheFile()
    {
        Queue<HttpResponseSnapshot> uploads = new([new HttpResponseSnapshot(200, "https://files.catbox.moe/aygdkm.bin", [])]);
        List<UploadCall> calls = [];
        List<(string Url, IReadOnlyDictionary<string, string> Form)> logins = [];
        CatboxPipeline pipeline = new(
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extraFields), headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            },
            // view.php returns the account page (userhash present); anything else is unexpected.
            getSnapshotOverride: (url, _) => new HttpResponseSnapshot(200, ViewHtml, []),
            // dologin.php sets a fresh authenticated PHPSESSID (like the live 302).
            postFormOverride: (url, form) => { logins.Add((url, new Dictionary<string, string>(form))); return new HttpResponseSnapshot(302, string.Empty, ["PHPSESSID=SESS1; path=/; domain=.catbox.moe"], "."); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://files.catbox.moe/aygdkm.bin", tc.FileUrl);

        // Logged in to dologin with the url-encoded credential form.
        (string loginUrl, IReadOnlyDictionary<string, string> loginForm) = Assert.Single(logins);
        Assert.Equal("https://catbox.moe/user/dologin.php", loginUrl);
        Assert.Equal("catuser", loginForm["username"]);
        Assert.Equal("catpass", loginForm["password"]);
        Assert.Equal("Login", loginForm["submit"]);

        // The upload carried the scraped userhash that links the file to the account.
        UploadCall call = Assert.Single(calls);
        Assert.Equal("fileupload", call.ExtraFields["reqtype"]);
        Assert.Equal("38a5e8a1bb24345b8055058c5", call.ExtraFields["userhash"]);
    }

    [Fact]
    public async Task RunAsync_Account_UsesOneLoginForTwoUploads()
    {
        Queue<HttpResponseSnapshot> uploads = new([
            new HttpResponseSnapshot(200, "https://files.catbox.moe/a.bin", []),
            new HttpResponseSnapshot(200, "https://files.catbox.moe/b.bin", []),
        ]);
        int logins = 0;
        CatboxPipeline pipeline = new(
            uploadOverride: (_, _, _, _, _) => Task.FromResult(uploads.Dequeue()),
            getSnapshotOverride: (_, _) => new HttpResponseSnapshot(200, ViewHtml, []),
            postFormOverride: (_, _) => { logins++; return new HttpResponseSnapshot(302, string.Empty, ["PHPSESSID=SESS1; path=/"], "."); });

        await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Equal(1, logins); // userhash cached per credentials id — second upload reuses it
    }

    [Fact]
    public async Task RunAsync_Account_LoginFails_YieldsAttemptFailedWithoutUpload()
    {
        // view.php has no userhash (unauthenticated page) → login treated as failed, upload never runs.
        bool uploadRan = false;
        CatboxPipeline pipeline = new(
            uploadOverride: (_, _, _, _, _) => { uploadRan = true; return Task.FromResult(new HttpResponseSnapshot(200, "https://files.catbox.moe/x.bin", [])); },
            getSnapshotOverride: (_, _) => new HttpResponseSnapshot(200, "<html>login page, not logged in</html>", []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["PHPSESSID=SESS1; path=/"], "."));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.False(uploadRan);
    }

    [Fact]
    public async Task CheckAccountAsync_ValidLogin_ScrapesUserhashAndReturnsValid()
    {
        CatboxPipeline pipeline = new(
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"),
            getSnapshotOverride: (_, _) => new HttpResponseSnapshot(200, ViewHtml, []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["PHPSESSID=SESS1; path=/"], "."));

        AccountCheckResult result = await pipeline.CheckAccountAsync("catuser", "catpass", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("catuser", result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccountAsync_WrongCredentials_ReturnsInvalid()
    {
        CatboxPipeline pipeline = new(
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"),
            getSnapshotOverride: (_, _) => new HttpResponseSnapshot(200, "<html>not logged in</html>", []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["PHPSESSID=SESS1; path=/"], "."));

        AccountCheckResult result = await pipeline.CheckAccountAsync("catuser", "wrong", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
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

    private static CatboxPipeline MakePipeline(Queue<HttpResponseSnapshot> uploads, out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;
        return new CatboxPipeline((filePath, endpoint, extraFields, headers, _) =>
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
        MakeContextWith(new FileHosterLoginDto { FileHosterName = "Catbox", IsAnonymous = true }, fileSize);

    private static AttemptContext MakeAccountContext(long fileSize = 1_048_576L) =>
        MakeContextWith(
            new FileHosterLoginDto { Id = 77, FileHosterName = "Catbox", Username = "catuser", Password = "catpass", IsAnonymous = false },
            fileSize);

    private static AttemptContext MakeContextWith(FileHosterLoginDto credentials, long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = fileSize,
        HosterName = "Catbox",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
