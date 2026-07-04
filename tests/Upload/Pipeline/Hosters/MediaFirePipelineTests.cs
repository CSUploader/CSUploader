// <copyright file="MediaFirePipelineTests.cs" company="CSUploader">
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
/// Orchestration tests for <see cref="MediaFirePipeline"/> — the web-login → session-token →
/// SHA-256 hash-dedup check → instant-link OR raw byte upload + poll flow. Every HTTP shape (GET,
/// urlencoded POST, raw-body upload) and the SHA-256 computation are stubbed via the test ctor, so
/// these lock in the step sequence, the auth/field wiring, the two upload branches, the share link,
/// and the failure/retry branches. The wire shapes themselves are verified against the live capture +
/// endpoint probing (see MediaFirePipeline.cs remarks).
/// </summary>
public class MediaFirePipelineTests
{
    private const string Hash = "6e75abee727bbdf7527532ef37766db13f71882d519e6668455d9e7010c71399";

    [Fact]
    public void Properties_DeclareMediaFireConfig()
    {
        MediaFirePipeline pipeline = new();
        Assert.Equal("MediaFire", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("MediaFire"));
    }

    [Fact]
    public async Task RunAsync_HashExists_LogsInThenInstantLinks()
    {
        FakeServer server = new();
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://www.mediafire.com/file/QK_INSTANT", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Login happened (GET /login/ then the client_login POST), then session token, check, instant.
        Assert.Equal(1, server.LoginPageGets);
        Assert.Equal(1, server.ClientLoginPosts);
        Assert.Equal(1, server.SessionTokenPosts);

        // The client_login POST forwarded the login-page cookie and carried the scraped security token.
        Assert.Contains("ukey=LOGINCOOKIE", server.LastClientLoginHeaders!["Cookie"], StringComparison.Ordinal);
        Assert.Equal("1783101596.securityhash", server.LastClientLoginForm!["security"]);
        Assert.Equal("me@example.com", server.LastClientLoginForm["login_email"]);

        // check.php carried the SHA-256 (in the uploads JSON) + the session token; no bytes were sent.
        Assert.Contains(Hash, server.LastCheckForm!["uploads"], StringComparison.Ordinal);
        Assert.Equal("SESSION_TOKEN", server.LastCheckForm["session_token"]);
        Assert.Equal("SESSION_TOKEN", server.LastInstantForm!["session_token"]);
        Assert.Equal(Hash, server.LastInstantForm["hash"]);
        Assert.Equal(0, server.SimpleUploads);
    }

    [Fact]
    public async Task RunAsync_HashMissing_UploadsRawBytesThenPolls()
    {
        FakeServer server = new() { HashExists = false };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://www.mediafire.com/file/QK_POLLED", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Raw byte upload to the check-provided simple URL, with the session token in the query and the
        // SHA-256 as the x-filehash header; then a poll resolved the quickkey.
        Assert.Equal(1, server.SimpleUploads);
        Assert.Contains("session_token=SESSION_TOKEN", server.LastUploadUrl!, StringComparison.Ordinal);
        Assert.Equal(Hash, server.LastUploadHeaders!["x-filehash"]);
        Assert.Equal(1, server.PollPosts);
        Assert.Equal("POLL_KEY", server.LastPollForm!["key"]);
        Assert.Equal(0, server.InstantPosts);
    }

    [Fact]
    public async Task RunAsync_SimpleUploadReturnsQuickKeyDirectly_SkipsPoll()
    {
        FakeServer server = new() { HashExists = false, SimpleReturnsQuickKeyDirectly = true };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://www.mediafire.com/file/QK_DIRECT", done.FileUrl);
        Assert.Equal(0, server.PollPosts); // quickkey was in the upload response — no poll needed
    }

    [Fact]
    public async Task RunAsync_StorageExceeded_FailsWithoutUploading()
    {
        FakeServer server = new() { HashExists = false, StorageExceeded = true };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("storage", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.SimpleUploads);
        Assert.Equal(0, server.InstantPosts);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_LoginRejected_FailsBeforeUpload()
    {
        FakeServer server = new() { LoginSucceeds = false };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("invalid login", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.SessionTokenPosts); // never got past login
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_SessionTokenRejected_FailsAndDropsCachedLogin()
    {
        FakeServer server = new() { SessionTokenSucceeds = false };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Single(events.OfType<AttemptFailed>());

        // A rejected session token drops the cached cookie jar, so the next attempt re-logs-in.
        server.SessionTokenSucceeds = true;
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        Assert.Equal(2, server.LoginPageGets); // re-login on the retry, not a reuse of the stale jar
    }

    [Fact]
    public async Task RunAsync_ReusesLoginAcrossUploads()
    {
        FakeServer server = new();
        MediaFirePipeline pipeline = server.Build();

        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // ONE login AND one session token, both shared across the two uploads — MediaFire caps active
        // session tokens per account, so minting one per file would invalidate the others.
        Assert.Equal(1, server.LoginPageGets);
        Assert.Equal(1, server.ClientLoginPosts);
        Assert.Equal(1, server.SessionTokenPosts);
    }

    [Fact]
    public async Task RunAsync_SessionTokenExpiresMidFlight_RefreshesSharedTokenAndCompletes()
    {
        // The shared token can age out; check.php returns error 105 once, the pipeline refreshes the
        // token and retries — the upload still completes.
        FakeServer server = new() { CheckSessionExpiresOnce = true };
        MediaFirePipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, server.SessionTokenPosts); // initial mint + one refresh after the 105
        Assert.Equal(2, server.CheckPosts);        // rejected once, then accepted
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send abort after setup must PROPAGATE (retryable) — MediaFire commits nothing until the
        // body is fully sent + polled, so the shared retry layer re-checks the hash and dedups.
        FakeServer server = new() { HashExists = false };
        server.UploadHandler = (_, _, _) =>
            throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054))));
        MediaFirePipeline pipeline = server.Build();

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    [Fact]
    public async Task CheckAccountAsync_ValidCredentials_ReturnsStorageAndTier()
    {
        FakeServer server = new();
        MediaFirePipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal(2_000_000L, result.StorageUsedBytes);
        Assert.Equal(1073741824L, result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_BadCredentials_ReturnsInvalid()
    {
        FakeServer server = new() { LoginSucceeds = false };
        MediaFirePipeline pipeline = server.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RefreshStorageAsync_ReturnsUsage()
    {
        FakeServer server = new();
        MediaFirePipeline pipeline = server.Build();

        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            MakeCredentials(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(2_000_000L, usage!.Value.UsedBytes);
        Assert.Equal(1073741824L, usage.Value.QuotaBytes);
    }

    /// <summary>
    /// A URL-routing fake for MediaFire's endpoints. Defaults model the capture's happy path
    /// (hash exists → instant); toggles flip individual legs (login/session failure, hash missing,
    /// storage exceeded, direct quickkey) for the branch tests.
    /// </summary>
    private sealed class FakeServer
    {
        public bool LoginSucceeds { get; set; } = true;

        public bool SessionTokenSucceeds { get; set; } = true;

        public bool HashExists { get; set; } = true;

        public bool StorageExceeded { get; set; }

        public bool SimpleReturnsQuickKeyDirectly { get; set; }

        /// <summary>First check.php returns the "session token expired" error (105); later calls succeed.</summary>
        public bool CheckSessionExpiresOnce { get; set; }

        public int LoginPageGets { get; private set; }

        public int ClientLoginPosts { get; private set; }

        public int SessionTokenPosts { get; private set; }

        public int CheckPosts { get; private set; }

        public int InstantPosts { get; private set; }

        public int PollPosts { get; private set; }

        public int SimpleUploads { get; private set; }

        public IReadOnlyDictionary<string, string>? LastClientLoginForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastClientLoginHeaders { get; private set; }

        public IReadOnlyDictionary<string, string>? LastCheckForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastInstantForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastPollForm { get; private set; }

        public string? LastUploadUrl { get; private set; }

        public IReadOnlyDictionary<string, string>? LastUploadHeaders { get; private set; }

        /// <summary>Overridable so a test can make the byte upload throw.</summary>
        public Func<string, IReadOnlyDictionary<string, string>, Func<long?>?, Task<HttpResponseSnapshot>>? UploadHandler { get; set; }

        public MediaFirePipeline Build() => new(Get, PostForm, Upload, Hash);

        private Task<HttpResponseSnapshot> Get(string url, IReadOnlyDictionary<string, string>? headers)
        {
            if (url.Contains("/login/", StringComparison.Ordinal))
            {
                LoginPageGets++;
                const string html = "<form id=\"form_login1\" method=\"post\">" +
                    "<input type=\"hidden\" name=\"security\" value=\"1783101596.securityhash\">" +
                    "<input name=\"login_email\"><input name=\"login_pass\"></form>";
                return Task.FromResult(new HttpResponseSnapshot(200, html, ["ukey=LOGINCOOKIE; path=/; domain=.mediafire.com"]));
            }

            return Task.FromResult(new HttpResponseSnapshot(404, "not found", []));
        }

        private Task<HttpResponseSnapshot> PostForm(string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string>? headers)
        {
            if (url.Contains("client_login", StringComparison.Ordinal))
            {
                ClientLoginPosts++;
                LastClientLoginForm = form;
                LastClientLoginHeaders = headers;
                return Task.FromResult(LoginSucceeds
                    ? new HttpResponseSnapshot(200, "{}", ["user=USERTOKEN; path=/; domain=.mediafire.com; HttpOnly"])
                    : new HttpResponseSnapshot(200, """{"action":10,"errorMessage":"You have entered an invalid login. (attempt 1 of 10)"}""", []));
            }

            if (url.Contains("get_session_token", StringComparison.Ordinal))
            {
                SessionTokenPosts++;
                return Task.FromResult(SessionTokenSucceeds
                    ? Ok("""{"response":{"session_token":"SESSION_TOKEN"}}""")
                    : Ok("""{"response":{"message":"The supplied Session Token is expired or invalid","error":105,"result":"Error"}}"""));
            }

            if (url.Contains("upload/check.php", StringComparison.Ordinal))
            {
                CheckPosts++;
                LastCheckForm = form;
                if (CheckSessionExpiresOnce && CheckPosts == 1)
                {
                    return Task.FromResult(Ok("""{"response":{"action":"upload/check","message":"The supplied Session Token is expired or invalid","error":105,"result":"Error"}}"""));
                }
                string checkBody = """{"response":{"action":"upload/check","hash_exists":"__HE__","file_exists":"no","storage_limit_exceeded":"__SE__","available_space":"1073741824","upload_url":{"simple":"https://www.mediafireuserupload.com/api/upload/simple.php"},"result":"Success"}}"""
                    .Replace("__HE__", HashExists ? "yes" : "no", StringComparison.Ordinal)
                    .Replace("__SE__", StorageExceeded ? "yes" : "no", StringComparison.Ordinal);
                return Task.FromResult(Ok(checkBody));
            }

            if (url.Contains("upload/instant.php", StringComparison.Ordinal))
            {
                InstantPosts++;
                LastInstantForm = form;
                return Task.FromResult(Ok("""{"response":{"action":"upload/instant","quickkey":"QK_INSTANT","result":"Success"}}"""));
            }

            if (url.Contains("poll_upload.php", StringComparison.Ordinal))
            {
                PollPosts++;
                LastPollForm = form;
                return Task.FromResult(Ok("""{"response":{"doupload":{"result":"0","status":"99","fileerror":"","quickkey":"QK_POLLED"}},"result":"Success"}"""));
            }

            if (url.Contains("user/get_info.php", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok("""{"response":{"user_info":{"premium":"no","used_storage_size":"2000000","storage_limit":"1073741824"},"result":"Success"}}"""));
            }

            return Task.FromResult(new HttpResponseSnapshot(404, "unrouted: " + url, []));
        }

        private Task<HttpResponseSnapshot> Upload(string url, IReadOnlyDictionary<string, string> headers, Func<long?>? bps)
        {
            SimpleUploads++;
            LastUploadUrl = url;
            LastUploadHeaders = headers;
            if (UploadHandler is not null)
            {
                return UploadHandler(url, headers, bps);
            }

            return Task.FromResult(SimpleReturnsQuickKeyDirectly
                ? Ok("""{"response":{"doupload":{"result":"0","quickkey":"QK_DIRECT"}}}""")
                : Ok("""{"response":{"doupload":{"result":"0","key":"POLL_KEY"}}}"""));
        }

        private static Task<string> Hash(string filePath, CancellationToken ct) => Task.FromResult(MediaFirePipelineTests.Hash);

        private static HttpResponseSnapshot Ok(string body) => new(200, body, []);
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

    private static FileHosterLoginDto MakeCredentials() => new()
    {
        Id = 7,
        FileHosterName = "MediaFire",
        Username = "me@example.com",
        Password = "pw",
    };

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "Free_Test_Data_5MB_AVI.avi",
        FileSize = 5_225_142,
        HosterName = "MediaFire",
        Credentials = MakeCredentials(),
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
