// <copyright file="PixeldrainPipelineTests.cs" company="CSUploader">
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
/// Orchestration tests for <see cref="PixeldrainPipeline"/> — the login (pd_auth_key cookie) → raw PUT
/// → <c>{"id":…}</c> flow. The login POST and the PUT upload are stubbed via the test ctor (the wire
/// shapes come from the live capture + endpoint probing), so these lock in the step sequence, the
/// auth/field wiring, the share link, login reuse, and the failure/retry branches.
/// </summary>
public class PixeldrainPipelineTests
{
    [Fact]
    public void Properties_DeclarePixeldrainConfig()
    {
        PixeldrainPipeline pipeline = new();
        Assert.Equal("Pixeldrain", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Pixeldrain"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_LogsInThenPutsAndReturnsLink()
    {
        FakeServer server = new();
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://pixeldrain.com/u/j53iEp3z", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Login carried username/password + app_name.
        Assert.Equal(1, server.LoginPosts);
        Assert.Equal("me@example.com", server.LastLoginForm!["username"]);
        Assert.Equal("CSUploader", server.LastLoginForm["app_name"]);

        // The PUT went to /api/file/<filename> with the pd_auth_key cookie.
        Assert.Equal(1, server.Uploads);
        Assert.EndsWith("/api/file/clip.avi", server.LastUploadUrl!, StringComparison.Ordinal);
        Assert.Equal("pd_auth_key=AUTHKEY", server.LastUploadHeaders!["Cookie"]);
    }

    [Fact]
    public async Task RunAsync_NonAsciiFilename_UrlEncodesThePath()
    {
        FakeServer server = new();
        PixeldrainPipeline pipeline = server.Build();

        AttemptContext ctx = MakeContext();
        ctx = ctx with { FileName = "元カレ (30m).mp4" };
        await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        // The path segment is percent-encoded (Uri.EscapeDataString) — no raw spaces or non-ASCII.
        Assert.DoesNotContain(" ", server.LastUploadUrl!, StringComparison.Ordinal);
        Assert.Contains("%20", server.LastUploadUrl!, StringComparison.Ordinal);
        Assert.Contains("%E5", server.LastUploadUrl!, StringComparison.Ordinal); // UTF-8 of a Japanese char
    }

    [Fact]
    public async Task RunAsync_ReusesLoginAcrossUploads()
    {
        FakeServer server = new();
        PixeldrainPipeline pipeline = server.Build();

        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // ONE login (pd_auth_key cached per credentials id), two uploads.
        Assert.Equal(1, server.LoginPosts);
        Assert.Equal(2, server.Uploads);
    }

    [Fact]
    public async Task RunAsync_LoginRejected_YieldsAttemptFailedWithoutUpload()
    {
        FakeServer server = new() { LoginSucceeds = false };
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("does not exist", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_UploadAuthExpired_DropsCachedKeyAndReLogsInNextTime()
    {
        // A 401 from the PUT means the cached pd_auth_key expired — the next attempt must re-login.
        FakeServer server = new() { UploadReturns401Once = true };
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> first = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Single(first.OfType<AttemptFailed>());

        List<UploadEvent> second = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Single(second.OfType<TransferCompleted>());
        Assert.Equal(2, server.LoginPosts); // re-login after the 401, not a reuse of the stale key
    }

    [Fact]
    public async Task RunAsync_UploadRejected_YieldsAttemptFailedWithoutCompletion()
    {
        FakeServer server = new();
        server.UploadHandler = (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
            413, """{"success":false,"value":"file_too_large","message":"This file is too large"}""", []));
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("too large", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send abort must PROPAGATE (retryable) — no file was committed, so the shared retry layer
        // re-uploads cleanly.
        FakeServer server = new();
        server.UploadHandler = (_, _, _, _) =>
            throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054))));
        PixeldrainPipeline pipeline = server.Build();

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    [Fact]
    public async Task CheckAccountAsync_ValidCredentials_LogsInAndReturnsApiKeyForPersistence()
    {
        FakeServer server = new();
        PixeldrainPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal("AUTHKEY", result.ApiKey); // the login auth_key is persisted as the API key
        Assert.Equal(1, server.LoginPosts);
    }

    [Fact]
    public async Task CheckAccountAsync_ValidStoredApiKey_ReusesItWithoutLoggingIn()
    {
        FakeServer server = new() { StoredKeyValid = true };
        PixeldrainPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", "STORED_KEY", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("STORED_KEY", result.ApiKey);
        Assert.Equal(1, server.SessionValidations); // validated via GET /api/user/session
        Assert.Equal(0, server.LoginPosts);          // no new session created
    }

    [Fact]
    public async Task CheckAccountAsync_StoredApiKeyRevoked_ReLogsInForAFreshKey()
    {
        FakeServer server = new() { StoredKeyValid = false };
        PixeldrainPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", "STALE_KEY", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("AUTHKEY", result.ApiKey); // regenerated
        Assert.Equal(1, server.LoginPosts);
    }

    [Fact]
    public async Task RunAsync_WithStoredApiKey_UploadsWithBasicAuthAndNoLogin()
    {
        FakeServer server = new();
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(apiKey: "MYKEY"), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(0, server.LoginPosts); // API key means no login round-trip
        Assert.Equal(1, server.Uploads);

        // Basic auth = base64(":"+key); base64(":MYKEY") = "Ok1ZS0VZ".
        Assert.Equal("Basic Ok1ZS0VZ", server.LastUploadHeaders!["Authorization"]);
        Assert.False(server.LastUploadHeaders.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task RunAsync_StoredApiKeyRejected_FallsBackToLoginCookie()
    {
        // A revoked stored key (401) must fall back to the original login-cookie upload path.
        int calls = 0;
        FakeServer server = new();
        server.UploadHandler = (_, _, headers, _) =>
        {
            calls++;
            if (headers!.ContainsKey("Authorization"))
            {
                return Task.FromResult(new HttpResponseSnapshot(401, """{"success":false,"value":"authentication_required","message":"…"}""", []));
            }

            // The fallback attempt carries the login cookie.
            Assert.Equal("pd_auth_key=AUTHKEY", headers["Cookie"]);
            return Task.FromResult(new HttpResponseSnapshot(201, """{"id":"okid"}""", []));
        };
        PixeldrainPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(apiKey: "BADKEY"), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://pixeldrain.com/u/okid", done.FileUrl);
        Assert.Equal(2, calls);            // API-key attempt (401) + cookie fallback (201)
        Assert.Equal(1, server.LoginPosts); // the fallback logged in
    }

    [Fact]
    public async Task CheckAccountAsync_BadCredentials_ReturnsInvalid()
    {
        FakeServer server = new() { LoginSucceeds = false };
        PixeldrainPipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    /// <summary>A URL-routing fake for pixeldrain's login POST and PUT upload. Defaults model the happy
    /// path (login → cookie → {"id":…}); toggles flip login/upload failures.</summary>
    private sealed class FakeServer
    {
        public bool LoginSucceeds { get; set; } = true;

        public bool UploadReturns401Once { get; set; }

        /// <summary>Whether GET /api/user/session (the stored-key validation) says the key is good.</summary>
        public bool StoredKeyValid { get; set; } = true;

        public int LoginPosts { get; private set; }

        public int Uploads { get; private set; }

        public int SessionValidations { get; private set; }

        public IReadOnlyDictionary<string, string>? LastLoginForm { get; private set; }

        public string? LastUploadUrl { get; private set; }

        public IReadOnlyDictionary<string, string>? LastUploadHeaders { get; private set; }

        public IReadOnlyDictionary<string, string>? LastValidationHeaders { get; private set; }

        /// <summary>Overridable so a test can make the PUT throw or return a specific verdict.</summary>
        public Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? UploadHandler { get; set; }

        public PixeldrainPipeline Build() => new(PostForm, Upload, Get);

        private HttpResponseSnapshot Get(string url, IReadOnlyDictionary<string, string>? headers)
        {
            SessionValidations++;
            LastValidationHeaders = headers;
            return StoredKeyValid
                ? new HttpResponseSnapshot(200, "[]", [])
                : new HttpResponseSnapshot(401, """{"success":false,"value":"authentication_required","message":"…"}""", []);
        }

        private HttpResponseSnapshot PostForm(string url, IReadOnlyDictionary<string, string> form)
        {
            LoginPosts++;
            LastLoginForm = form;
            return LoginSucceeds
                ? new HttpResponseSnapshot(201, "{}", ["pd_auth_key=AUTHKEY; Path=/; Expires=Sun, 21 Jun 2076 10:10:20 GMT"])
                : new HttpResponseSnapshot(404, """{"success":false,"value":"user_not_found","message":"User with this name or e-mail address does not exist"}""", []);
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

            if (UploadReturns401Once && Uploads == 1)
            {
                return Task.FromResult(new HttpResponseSnapshot(401, """{"success":false,"value":"authentication_required","message":"This request requires API authentication."}""", []));
            }

            return Task.FromResult(new HttpResponseSnapshot(201, """{"id":"j53iEp3z"}""", []));
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

    private static AttemptContext MakeContext(string? apiKey = null) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_225_142,
        HosterName = "Pixeldrain",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Pixeldrain", Username = "me@example.com", Password = "pw", ApiKey = apiKey },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
