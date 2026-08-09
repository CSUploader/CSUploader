// <copyright file="DepositFilesPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// DepositFiles — an account-only JSON API signed into through the app's browser. Every fixture is a
/// real response: the node call and the upload reply from live probes, the upload-page markup from a
/// browser capture. Session values and the passkey are faked.
/// <para>
/// The behaviour most of these exist to protect is the passkey rule: the upload succeeds and returns
/// a working link WITHOUT it, and the file simply isn't the account's.
/// </para>
/// </summary>
public class DepositFilesPipelineTests : IDisposable
{
    private const string NodeJson = """
        {"status":"OK","status_code":1,"data":{"upload_url":"https:\/\/fileshare2131.depositfiles.com\/FS213-1u\/?X-Progress-ID=5106d41b9802ab4e4af63fe489e9fc91","progress_url":"https:\/\/fileshare2131.depositfiles.com\/progress?X-Progress-ID=5106d41b9802ab4e4af63fe489e9fc91","max_file_size_mb":"10240"}}
        """;

    private const string UploadOkJson = """
        {"status":"OK","status_code":1,"download_url":"http:\/\/depositfiles.com\/files\/nnddn3823","delete_url":"http:\/\/depositfiles.com\/rmv\/2771297708126336"}
        """;

    /// <summary>The upload page's own markup — where the passkey lives for a session that never saw
    /// the login JSON (i.e. one captured by the sign-in browser).</summary>
    private const string UploadPageHtml = """
        <div id="container_upload" sharedkey="76oecg2ydcf4gl6s">
          <form action="https://fileshare2131.depositfiles.com/FS213-1u/?X-Progress-ID=8c129dfb">
            <input type="hidden" name="MAX_FILE_SIZE" value="10737418240"/>
          </form>
        </div>
        """;

    private const string LoginInvalidJson = """{"status":"Error","status_code":0,"error":"LoginInvalid","error_code":101}""";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "csu-df-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public DepositFilesPipelineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }

    // ── Identity and wiring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepositFiles_IsAccountOnly_AndWiredIntoTheApp()
    {
        DepositFilesPipeline pipeline = new();

        Assert.Equal("DepositFiles", pipeline.Name);

        // Measured, not read off the page: the node call answers LoginInvalid to a caller with no
        // session. (Its signed-out upload page says the same in words.)
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.Equal(10L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);

        // Session-cookie family: the app's browser is the ONLY sign-in path here, and no password is
        // stored. Not because there's no login API to post — there is, and it works — but because it
        // is captcha-gated on the host's own risk assessment, so a password path would work until one
        // day it didn't, mid-Save. There is also no key to paste: the passkey the upload needs is
        // derived behind the sign-in, never typed.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("DepositFiles"));
        Assert.True(HosterCredentialModes.IsSessionCookieHoster("DepositFiles"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster("DepositFiles"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("DepositFiles"));
        Assert.Equal("depositfiles.com", FileHosterClient.FileHosters["DepositFiles"]);
    }

    // ── The node lookup ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseUploadNode_StripsTheProgressQuery_AndRewritesFsToUploadFs()
    {
        // Both halves are the site's own rule (uploadURL.replace(/[?].*/,'') then
        // .replace('/FS','/upload/FS')), and the capture posts to exactly this.
        (string? node, string? error) = DepositFilesPipeline.ParseUploadNode(new HttpResponseSnapshot(200, NodeJson, []));

        Assert.Null(error);
        Assert.Equal("https://fileshare2131.depositfiles.com/upload/FS213-1u/", node);
    }

    [Fact]
    public void ParseUploadNode_ASessionThatHasLapsed_SaysToRecheckTheAccount()
    {
        // 101 is the same code a signed-out caller gets, and it is the one failure here the user can
        // actually act on.
        (string? node, string? error) = DepositFilesPipeline.ParseUploadNode(new HttpResponseSnapshot(200, LoginInvalidJson, []));

        Assert.Null(node);
        Assert.Contains("no longer valid", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(503, NodeJson, "503")]
    [InlineData(200, """{"status":"Error","status_code":0,"error":"Overloaded","error_code":7}""", "Overloaded")]
    [InlineData(200, """{"status":"OK","status_code":1,"data":{"max_file_size_mb":"10240"}}""", "no upload_url")]
    [InlineData(200, """{"status":"OK","status_code":1,"data":{"upload_url":"https://fileshare1.depositfiles.com/nope/"}}""", "/FS")]
    [InlineData(200, "<html>maintenance</html>", "wasn't JSON")]
    public void ParseUploadNode_RefusesAnythingThatIsNotANode(int status, string body, string fragment)
    {
        (string? node, string? error) = DepositFilesPipeline.ParseUploadNode(new HttpResponseSnapshot(status, body, []));

        Assert.Null(node);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The upload reply ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseUploadResponse_ReadsBothLinks_AndUpgradesThemToHttps()
    {
        // Its API answers with http:// links and the site 301s them; emitting the secure one saves
        // every downloader a redirect.
        (string? link, string? delete, string? error) = DepositFilesPipeline.ParseUploadResponse(
            new HttpResponseSnapshot(200, UploadOkJson, []));

        Assert.Null(error);
        Assert.Equal("https://depositfiles.com/files/nnddn3823", link);
        Assert.Equal("https://depositfiles.com/rmv/2771297708126336", delete);
    }

    [Theory]
    [InlineData(500, "<html>500</html>", "rejected the upload")]
    [InlineData(200, """{"status":"Error","status_code":0,"error":"FileTooBig","error_code":9}""", "FileTooBig")]
    [InlineData(200, """{"status":"OK","status_code":1}""", "no link")]
    [InlineData(200, "not json", "wasn't JSON")]
    public void ParseUploadResponse_ExplainsEveryFailure(int status, string body, string fragment)
    {
        (string? link, _, string? error) = DepositFilesPipeline.ParseUploadResponse(new HttpResponseSnapshot(status, body, []));

        Assert.Null(link);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The passkey ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSharedKey_ReadsTheAccountsUploadKeyOffThePage()
    {
        Assert.Equal("76oecg2ydcf4gl6s", DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(200, UploadPageHtml, [])));
        Assert.Null(DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(200, "<div id=\"container_upload\">", [])));
        Assert.Null(DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(302, UploadPageHtml, [], "https://depositfiles.com/login.php")));
    }

    // ── Uploading ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Anonymously_IsRefusedLocally_WithoutAskingTheHost()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder) with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "DepositFiles", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no anonymous upload", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Gets);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_WithNoSavedSession_SaysSo_WithoutAskingTheHost()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder, sessionCookie: null);

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no saved sign-in", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Gets);
    }

    [Fact]
    public async Task Run_AFileOverTheCap_IsRefusedWithoutAnyHttp()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder) with { FileSize = (10L * 1024 * 1024 * 1024) + 1 };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("10 GiB", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Gets);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_WithAStoredPasskey_UploadsWithoutFetchingTheUploadPage()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder, passkey: "76oecg2ydcf4gl6s");

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal("https://depositfiles.com/files/nnddn3823", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // Only the node call — the page scrape is what a session with no stored key needs.
        Assert.Equal(["https://depositfiles.com/api/upload/regular"], recorder.Gets.Select(g => g.Url).ToArray());

        UploadCall call = Assert.Single(recorder.Uploads);
        Assert.Equal("https://fileshare2131.depositfiles.com/upload/FS213-1u/", call.Endpoint);
        Assert.Equal("76oecg2ydcf4gl6s", call.Fields["member_passkey"]);
        Assert.Equal("html5", call.Fields["format"]);
        Assert.Equal("_root", call.Fields["fm"]);
        Assert.Equal(string.Empty, call.Fields["fmh"]);
    }

    [Fact]
    public async Task Run_WithNoStoredPasskey_ScrapesItOffTheUploadPageFirst()
    {
        // The path a browser sign-in leaves behind: it captures the cookie but never sees the login
        // JSON the passkey normally arrives in.
        Recorder recorder = new();

        await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder), CancellationToken.None));

        Assert.Contains(recorder.Gets, g => g.Url == "https://depositfiles.com/?upload=1");
        Assert.Equal("76oecg2ydcf4gl6s", Assert.Single(recorder.Uploads).Fields["member_passkey"]);

        // And the page fetch carried the session, or it would have been the visitor's page.
        Assert.Equal("autologin=s3ss10n-va1ue", recorder.Gets.First(g => g.Url.Contains("upload=1", StringComparison.Ordinal)).Headers["Cookie"]);
    }

    [Fact]
    public async Task Run_WhenThePasskeyCannotBeFound_UploadsNothingAtAll()
    {
        // THE guard: this host takes the file and returns a working link with an empty passkey, and
        // the file is then absent from the account's own listing. Uploading anyway would hand the user
        // a link they don't own and can't manage.
        Recorder recorder = new() { UploadPage = new HttpResponseSnapshot(200, "<div id=\"container_upload\">", []) };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder), CancellationToken.None));

        Assert.Contains("upload key", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_ANodeLookupThatFails_StopsBeforeSendingAnyBytes()
    {
        Recorder recorder = new() { Node = new HttpResponseSnapshot(200, LoginInvalidJson, []) };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder, passkey: "k"), CancellationToken.None));

        Assert.Contains("no longer valid", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_LogsTheDeleteLink()
    {
        Mock<IAppLogger> logger = new();
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder, passkey: "k") with { Logger = logger.Object };

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        logger.Verify(
            l => l.Log(
                It.IsAny<object>(),
                It.IsAny<LogType>(),
                It.Is<string>(m => m.Contains("/rmv/2771297708126336", StringComparison.Ordinal)),
                It.IsAny<HttpTransaction?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()),
            Times.Once);
    }

    // ── Signing in ────────────────────────────────────────────────────────────────────────────────

    // ── Signing in ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAccount_SignsInThroughTheBrowser_ThenScrapesThePasskey()
    {
        // The browser never sees the login JSON that member_passkey normally arrives in, so it comes
        // off the upload page — which is the whole reason that scrape exists.
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), "csuprobe", It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult("s3ss10n-from-browser", null));

        List<(string Url, IReadOnlyDictionary<string, string> Headers)> gets = [];
        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (url, headers) => { gets.Add((url, headers)); return Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])); });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "ignored", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10n-from-browser", result.SessionCookie);
        Assert.Equal("76oecg2ydcf4gl6s", result.ApiKey);

        // A year, which is what the host sets Max-Age to — and why one browser window is a rare cost.
        Assert.True(result.SessionCookieExpiresUtc > DateTime.UtcNow.AddDays(300));

        auth.Verify(
            a => a.AcquireSessionCookieAsync(
                It.Is<InteractiveAuthSpec>(s => s.CookieName == "autologin" && s.LoginUrl == "https://depositfiles.com/login.php"),
                "csuprobe",
                It.IsAny<ProxyChoice?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The scrape has to carry the captured session or it reads the visitor's page.
        Assert.Equal("autologin=s3ss10n-from-browser", Assert.Single(gets).Headers["Cookie"]);
    }

    [Fact]
    public async Task CheckAccount_NeverAsksForAPassword_EvenWhenGivenOne()
    {
        // This host HAS a login API this app could post, and it works — until it answers
        // CaptchaRequired on the host's own risk assessment. A password path would therefore work
        // until one day it didn't, halfway through saving an account, so there isn't one.
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult("s3ss10n-from-browser", null));

        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])));

        // Empty credentials must reach the browser just the same — the dialog collects neither.
        Assert.True((await pipeline.CheckAccountAsync(
            string.Empty, string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None)).IsValid);

        auth.Verify(
            a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAccount_WithNoBrowserAvailable_SaysWhatToDo()
    {
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => throw new InvalidOperationException("nothing to fetch"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("embedded browser", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_ACancelledSignIn_IsNotAnAccount()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InteractiveAuthResult?)null);

        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (_, _) => throw new InvalidOperationException("nothing to fetch"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("cancelled", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_ASignInThatYieldsNoPasskey_IsNotAnAccount()
    {
        // A session with no passkey can upload — and every file would belong to nobody. Saving it
        // would be saving an account that quietly doesn't work.
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult("s3ss10n-from-browser", null));

        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, "<div id=\"container_upload\">", [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("upload key", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Re-checking a stored session ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAccount_RereadsThePasskey_WithoutNeedingThePassword()
    {
        // Not just a liveness check: the passkey is re-read every time, so an account saved before it
        // was stored — or one whose key the host rotates — heals itself on the next check.
        List<(string Url, IReadOnlyDictionary<string, string> Headers)> gets = [];
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (url, headers) => { gets.Add((url, headers)); return Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])); });

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, "s3ss10n-va1ue", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10n-va1ue", result.SessionCookie);
        Assert.Equal("76oecg2ydcf4gl6s", result.ApiKey);
        Assert.Equal("autologin=s3ss10n-va1ue", Assert.Single(gets).Headers["Cookie"]);
    }

    [Fact]
    public async Task RefreshAccount_AnExpiredSession_AsksForAFreshSignIn()
    {
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(302, string.Empty, [], "https://depositfiles.com/login.php")));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, "expired", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("sign in again", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccount_AHostThatIsDown_DoesNotClaimTheSessionIsGood()
    {
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => throw new HttpRequestException("no such host"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, "s3ss10n-va1ue", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

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

    private AttemptContext MakeContext(Recorder recorder, string? sessionCookie = "s3ss10n-va1ue", string? passkey = null)
    {
        string path = Path.Combine(_tempDir, "release.r00");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, new byte[4096]);
        }

        return new AttemptContext
        {
            AttemptId = Guid.NewGuid(),
            FilePath = path,
            FileName = "release.r00",
            FileSize = 4096,
            HosterName = "DepositFiles",
            Credentials = new FileHosterLoginDto
            {
                Id = 1,
                FileHosterName = "DepositFiles",
                IsAnonymous = false,
                Username = "csuprobe",
                SessionCookie = sessionCookie,
                ApiKey = passkey,
            },
            Proxy = ProxyChoice.Direct,
            Handler = MakeHandler(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };
    }

    /// <summary>Stands in for the host and records what it was sent.</summary>
    private sealed class Recorder
    {
        public Recorder() => Pipeline = new DepositFilesPipeline(null, GetAsync, UploadAsync);

        public DepositFilesPipeline Pipeline { get; }

        public List<(string Url, IReadOnlyDictionary<string, string> Headers)> Gets { get; } = [];

        public List<UploadCall> Uploads { get; } = [];

        public HttpResponseSnapshot Node { get; init; } = new(200, NodeJson, []);

        public HttpResponseSnapshot UploadPage { get; init; } = new(200, UploadPageHtml, []);

        private Task<HttpResponseSnapshot> GetAsync(string url, IReadOnlyDictionary<string, string> headers)
        {
            Gets.Add((url, headers));
            return Task.FromResult(url.Contains("api/upload/regular", StringComparison.Ordinal) ? Node : UploadPage);
        }

        private Task<HttpResponseSnapshot> UploadAsync(string filePath, string endpoint, IReadOnlyDictionary<string, string> fields)
        {
            Uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        }
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
