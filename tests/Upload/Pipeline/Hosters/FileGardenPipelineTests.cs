// <copyright file="FileGardenPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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
/// Orchestration tests for <see cref="FileGardenPipeline"/> — the login (auth cookie + userId) → raw POST
/// to <c>/users/&lt;userId&gt;/pipe</c> → <c>{"id","path"}</c> flow. The login JSON POST and the raw
/// upload are stubbed via the test ctor (wire shapes come from the live capture + probing), so these lock
/// in the step sequence, the auth/field wiring, the share link, login reuse, and the failure branches.
/// </summary>
public class FileGardenPipelineTests
{
    [Fact]
    public void Properties_DeclareFileGardenConfig()
    {
        FileGardenPipeline pipeline = new();
        Assert.Equal("FileGarden", pipeline.Name);
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSize); // File Garden's 100 MiB per-file cap
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("FileGarden"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_LogsInThenPostsAndReturnsLink()
    {
        FakeServer server = new();
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://file.garden/USER123/clip.avi", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Login carried connection=password base64(pw) + email.
        Assert.Equal(1, server.LoginPosts);
        Assert.Contains("\"connection\":\"password " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pw")), server.LastLoginJson!, StringComparison.Ordinal);
        Assert.Contains("\"email\":\"me@example.com\"", server.LastLoginJson!, StringComparison.Ordinal);

        // The POST went to /users/<userId>/pipe with the auth cookie and the X-Data metadata header.
        Assert.Equal(1, server.Uploads);
        Assert.EndsWith("/users/USER123/pipe", server.LastUploadUrl!, StringComparison.Ordinal);
        Assert.Equal("auth=AUTHCOOKIE", server.LastUploadHeaders!["Cookie"]);
        // X-Data is encodeURI-style: {}"" percent-encoded, but ':' and ',' left literal (NOT %3A/%2C).
        Assert.Equal("%7B%22parent%22:null,%22name%22:%22clip.avi%22%7D", server.LastUploadHeaders["X-Data"]);
    }

    [Fact]
    public async Task RunAsync_NonAsciiFilename_EncodesXDataAndLink()
    {
        FakeServer server = new() { ResponsePath = "元カレ.mp4" };
        FileGardenPipeline pipeline = server.Build();

        AttemptContext ctx = MakeContext() with { FileName = "元カレ.mp4" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        // X-Data (encodeURI-style) round-trips to the raw JSON, with ':'/',' left literal.
        string xData = server.LastUploadHeaders!["X-Data"];
        Assert.Equal("""{"parent":null,"name":"元カレ.mp4"}""", Uri.UnescapeDataString(xData));
        Assert.Contains(":null,", xData, StringComparison.Ordinal); // proves encodeURI, not encodeURIComponent
        // The link percent-encodes the path segment too (encodeURIComponent == EscapeDataString here — the
        // name has no parentheses).
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://file.garden/USER123/" + Uri.EscapeDataString("元カレ.mp4"), done.FileUrl);
    }

    [Fact]
    public async Task RunAsync_PublicLink_UsesFileGardenDomainWithBase64GardenIdAndLiteralParens()
    {
        // Real Mongo-ObjectId userId → garden id = URL-safe base64 of its 12 bytes; the domain is
        // file.garden (NOT filegarden.com/<userId>); and parentheses/spaces match File Garden's own links
        // (encodeURIComponent keeps '(' ')', escapes spaces — Uri.EscapeDataString would wrongly give %28).
        FakeServer server = new()
        {
            LoginBody = """{"id":"6a48f13c4eeb016f7cc26c02","token":"Tok"}""",
            ResponsePath = "clip (2024).mkv", // the success link is built from the server's returned path
        };
        FileGardenPipeline pipeline = server.Build();

        AttemptContext ctx = MakeContext() with { FileName = "clip (2024).mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://file.garden/akjxPE7rAW98wmwC/clip%20(2024).mkv", done.FileUrl);
    }

    [Fact]
    public async Task RunAsync_FileOver100MiB_FailsFastWithoutLoginOrUpload()
    {
        // File Garden rejects >100 MiB (a bigger POST 413s at the Cloudflare edge). Fail before any
        // login/list/upload rather than waste the transfer.
        FakeServer server = new();
        FileGardenPipeline pipeline = server.Build();

        AttemptContext ctx = MakeContext() with { FileSize = (100L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("100 MiB", fail.Reason, StringComparison.Ordinal);
        Assert.Equal(0, server.LoginPosts);
        Assert.Equal(0, server.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_ReusesLoginAcrossUploads()
    {
        FakeServer server = new();
        FileGardenPipeline pipeline = server.Build();

        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // ONE login (auth + userId cached per credentials id), two uploads.
        Assert.Equal(1, server.LoginPosts);
        Assert.Equal(2, server.Uploads);
    }

    [Fact]
    public async Task RunAsync_SameFileAlreadyExists_SkipsUploadAndReturnsExistingLink()
    {
        // The garden root already holds clip.avi at the same size → the pre-check short-circuits: no
        // upload, and the existing file's link is returned.
        FakeServer server = new()
        {
            ListItemsBody = """{"ancestors":[],"items":[{"id":"OLD","name":"clip.avi","path":"clip.avi","size":5225142,"type":"video/x-msvideo"}]}""",
        };
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://file.garden/USER123/clip.avi", done.FileUrl);
        Assert.Equal(1, server.ListChecks);
        Assert.Equal(0, server.Uploads); // NOT re-uploaded
        Assert.Empty(events.OfType<AttemptFailed>());
        // The pre-check GET was authenticated (owner's private files).
        Assert.Equal("auth=AUTHCOOKIE", server.LastListHeaders!["Cookie"]);
    }

    [Fact]
    public async Task RunAsync_DifferentFileSameName_FailsWithoutUploading()
    {
        // Same name, DIFFERENT size → a genuine clash File Garden won't let us resolve; fail clearly
        // rather than upload-then-422 or return a wrong link.
        FakeServer server = new()
        {
            ListItemsBody = """{"ancestors":[],"items":[{"id":"OLD","name":"clip.avi","path":"clip.avi","size":999,"type":"video/x-msvideo"}]}""",
        };
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("already exists", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Uploads);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_Upload422AlreadyExists_ReturnsLinkAsSuccess()
    {
        // Safety net: the pre-check saw the name free (race) but the upload 422s "already exists" — treat
        // it as done and return the (root path == name) link rather than failing.
        FakeServer server = new();
        server.UploadHandler = (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
            422,
            """{"error":"<b>clip.avi</b> already exists in the specified directory."}""",
            []));
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://file.garden/USER123/clip.avi", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_UnverifiedEmail_YieldsAttemptFailedWithoutUpload()
    {
        FakeServer server = new() { LoginStatus = 422, LoginBody = """{"error":"That email is not verified.","unverified":true}""" };
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("not verified", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_UploadAuthExpired_DropsCachedSessionAndReLogsInNextTime()
    {
        // A 403 from the pipe means the cached auth cookie expired — the next attempt must re-login.
        FakeServer server = new() { UploadReturns403Once = true };
        FileGardenPipeline pipeline = server.Build();

        List<UploadEvent> first = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Single(first.OfType<AttemptFailed>());

        List<UploadEvent> second = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Single(second.OfType<TransferCompleted>());
        Assert.Equal(2, server.LoginPosts); // re-login after the 403
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        FakeServer server = new();
        server.UploadHandler = (_, _, _, _) =>
            throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054))));
        FileGardenPipeline pipeline = server.Build();

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    [Fact]
    public async Task CheckAccountAsync_ValidCredentials_ReturnsValid()
    {
        FakeServer server = new();
        FileGardenPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
    }

    [Fact]
    public async Task CheckAccountAsync_BadCredentials_ReturnsInvalid()
    {
        FakeServer server = new() { LoginStatus = 422, LoginBody = """{"error":"That email is not registered."}""" };
        FileGardenPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "nobody@example.invalid", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    /// <summary>A fake for File Garden's login JSON POST and pipe upload. Defaults model the happy path
    /// (201 login → 201 pipe {"id","path"}); toggles flip login/upload failures.</summary>
    private sealed class FakeServer
    {
        public int LoginStatus { get; set; } = 201;

        public string LoginBody { get; set; } = """{"id":"USER123","token":"Tok"}""";

        public string ResponsePath { get; set; } = "clip.avi";

        public bool UploadReturns403Once { get; set; }

        /// <summary>The garden-root list the pre-check GET returns. Default: empty (name is free).</summary>
        public string ListItemsBody { get; set; } = """{"ancestors":[],"items":[]}""";

        public int LoginPosts { get; private set; }

        public int Uploads { get; private set; }

        public int ListChecks { get; private set; }

        public string? LastLoginJson { get; private set; }

        public string? LastUploadUrl { get; private set; }

        public IReadOnlyDictionary<string, string>? LastUploadHeaders { get; private set; }

        public IReadOnlyDictionary<string, string>? LastListHeaders { get; private set; }

        /// <summary>Overridable so a test can make the upload throw or return a specific verdict.</summary>
        public Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? UploadHandler { get; set; }

        public FileGardenPipeline Build() => new(PostJson, Upload, Get);

        private HttpResponseSnapshot Get(string url, IReadOnlyDictionary<string, string>? headers)
        {
            ListChecks++;
            LastListHeaders = headers;
            return new HttpResponseSnapshot(200, ListItemsBody, []);
        }

        private HttpResponseSnapshot PostJson(string url, string? json, IReadOnlyDictionary<string, string>? headers)
        {
            LoginPosts++;
            LastLoginJson = json;
            return LoginStatus is >= 200 and < 300
                ? new HttpResponseSnapshot(LoginStatus, LoginBody, ["auth=AUTHCOOKIE; Domain=.filegarden.com; Path=/; HttpOnly; Secure"])
                : new HttpResponseSnapshot(LoginStatus, LoginBody, []);
        }

        private Task<HttpResponseSnapshot> Upload(string filePath, string url, IReadOnlyDictionary<string, string>? headers, Func<long?>? bps)
        {
            Uploads++;
            LastUploadUrl = url;
            LastUploadHeaders = headers;
            if (UploadHandler is not null)
            {
                return UploadHandler(filePath, url, headers, bps);
            }

            if (UploadReturns403Once && Uploads == 1)
            {
                return Task.FromResult(new HttpResponseSnapshot(403, """{"error":"You do not have permission to access that user's garden."}""", []));
            }

            return Task.FromResult(new HttpResponseSnapshot(
                201,
                "{\"id\":\"FILEID\",\"path\":\"" + ResponsePath + "\",\"type\":\"video/x-msvideo\",\"size\":5225142,\"privacy\":1}",
                []));
        }
    }

    private static async Task<List<UploadEvent>> Drain(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_225_142,
        HosterName = "FileGarden",
        Credentials = new FileHosterLoginDto { Id = 12, FileHosterName = "FileGarden", Username = "me@example.com", Password = "pw" },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
