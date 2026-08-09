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
/// DepositFiles — an account-only JSON API. Every fixture is a real response: the node call, the
/// upload reply and the login envelopes came from live probes, the upload-page markup from a browser
/// capture. Session values, the passkey and the account name are faked.
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

    private const string LoginOkJson = """
        {"status":"OK","status_code":1,"data":{"user_id":"csuprobe","username":"csuprobe","email":"csuprobe@example.test","mode":"free","gold_expired":null,"token":"FAKE-T0KEN","member_passkey":"76oecg2ydcf4gl6s","is_reseller":"N","active_package":"basic"}}
        """;

    private const string CaptchaRequiredJson = """{"status":"Error","status_code":0,"error":"CaptchaRequired","error_code":104}""";
    private const string LoginInvalidJson = """{"status":"Error","status_code":0,"error":"LoginInvalid","error_code":101}""";

    private const string SessionCookie = "autologin=s3ss10n-va1ue; expires=Mon, 09-Aug-2027 11:54:26 GMT; Max-Age=31536000; domain=.depositfiles.com; path=/";

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

        // Username and password, NOT the WebView families: the plain login post is the normal path,
        // and the browser only opens if the host asks for a captcha.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("DepositFiles"));
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

    // ── The passkey and the session cookie ────────────────────────────────────────────────────────

    [Fact]
    public void ParseSharedKey_ReadsTheAccountsUploadKeyOffThePage()
    {
        Assert.Equal("76oecg2ydcf4gl6s", DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(200, UploadPageHtml, [])));
        Assert.Null(DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(200, "<div id=\"container_upload\">", [])));
        Assert.Null(DepositFilesPipeline.ParseSharedKey(new HttpResponseSnapshot(302, UploadPageHtml, [], "https://depositfiles.com/login.php")));
    }

    [Fact]
    public void ReadSessionCookie_TakesAutologin_AndIgnoresTheOtherOne()
    {
        // The login sets two long cookies; only autologin authenticates (asked with just the al_<hash>
        // one, the node call answers LoginInvalid).
        HttpResponseSnapshot login = new(
            200,
            LoginOkJson,
            [
                "al_cfc0382ef75989d311f86a8815c2d880=un-us4b1e; Max-Age=31536000; path=/",
                SessionCookie,
            ]);

        Assert.Equal("s3ss10n-va1ue", DepositFilesPipeline.ReadSessionCookie(login));
    }

    [Theory]
    [InlineData("al_cfc0382ef75989d311f86a8815c2d880=whatever; path=/")]
    [InlineData("autologin=; path=/")]
    [InlineData("autologin=deleted; path=/")]
    public void ReadSessionCookie_IsNullWhenNoUsableSessionWasIssued(string setCookie)
        => Assert.Null(DepositFilesPipeline.ReadSessionCookie(new HttpResponseSnapshot(200, LoginOkJson, [setCookie])));

    // ── The login envelope ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseLogin_ASuccess_YieldsTheSessionThePasskeyAndTheName()
    {
        (string? session, string? passkey, string? name, AccountType type, int? code, string? message) =
            DepositFilesPipeline.ParseLogin(new HttpResponseSnapshot(200, LoginOkJson, [SessionCookie]));

        Assert.Equal("s3ss10n-va1ue", session);
        Assert.Equal("76oecg2ydcf4gl6s", passkey);
        Assert.Equal("csuprobe", name);
        Assert.Equal(AccountType.Free, type);
        Assert.Null(code);
        Assert.Null(message);
    }

    [Fact]
    public void ParseLogin_TheCaptchaWall_IsToldApartFromAWrongPassword()
    {
        // The whole reason this distinction exists: 104 means "a human must solve something", which is
        // recoverable in the browser, while 101 means the credentials are wrong and no window helps.
        (_, _, _, _, int? captchaCode, string? captchaMessage) =
            DepositFilesPipeline.ParseLogin(new HttpResponseSnapshot(200, CaptchaRequiredJson, []));

        Assert.Equal(104, captchaCode);
        Assert.Contains("captcha", captchaMessage!, StringComparison.OrdinalIgnoreCase);

        (_, _, _, _, int? badCode, string? badMessage) =
            DepositFilesPipeline.ParseLogin(new HttpResponseSnapshot(200, LoginInvalidJson, []));

        Assert.Equal(101, badCode);
        Assert.Contains("username and password", badMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLogin_ASignInWithoutAPasskey_IsNotUsable()
    {
        // The passkey is what files uploads under the account. A session without one would upload
        // successfully into nobody's account, so it must not count as signed in.
        const string NoKey = """{"status":"OK","status_code":1,"data":{"username":"csuprobe","mode":"free"}}""";

        (string? session, string? passkey, _, _, _, string? message) =
            DepositFilesPipeline.ParseLogin(new HttpResponseSnapshot(200, NoKey, [SessionCookie]));

        Assert.Null(session);
        Assert.Null(passkey);
        Assert.Contains("no usable session", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLogin_APaidTier_IsReportedAsPremium()
    {
        (_, _, _, AccountType type, _, _) = DepositFilesPipeline.ParseLogin(
            new HttpResponseSnapshot(200, LoginOkJson.Replace("\"mode\":\"free\"", "\"mode\":\"gold\"", StringComparison.Ordinal), [SessionCookie]));

        Assert.Equal(AccountType.Premium, type);
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

    [Fact]
    public async Task CheckAccount_PostsThePlainLogin_WithTheCaptchaFieldsEmpty_AndNoBrowser()
    {
        // authService is null, so any attempt to open a window fails the check outright — which is the
        // assertion. The site posts these four fields empty itself when it isn't asking for a captcha.
        List<(string Url, IReadOnlyDictionary<string, string> Form)> posts = [];
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])),
            postFormOverride: (url, form, _) =>
            {
                posts.Add((url, form));
                return Task.FromResult(new HttpResponseSnapshot(200, LoginOkJson, [SessionCookie]));
            });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10n-va1ue", result.SessionCookie);
        Assert.Equal("76oecg2ydcf4gl6s", result.ApiKey);
        Assert.Equal("csuprobe", result.DerivedUsername);
        Assert.Equal(AccountType.Free, result.AccountType);

        // A year, which is what the host sets Max-Age to — and why the captcha fallback is rare.
        Assert.True(result.SessionCookieExpiresUtc > DateTime.UtcNow.AddDays(300));

        (string url, IReadOnlyDictionary<string, string> form) = Assert.Single(posts);
        Assert.Equal("https://depositfiles.com/api/user/login", url);
        Assert.Equal("csuprobe", form["login"]);
        Assert.Equal("hunter2", form["password"]);
        Assert.Equal(string.Empty, form["cf-turnstile-response"]);
        Assert.Equal(string.Empty, form["g-recaptcha-response"]);
    }

    [Fact]
    public async Task CheckAccount_WrongPassword_FailsWithoutOpeningTheBrowser()
    {
        Mock<IInteractiveAuthService> auth = new(MockBehavior.Strict);
        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (_, _) => throw new InvalidOperationException("no page fetch on a failed login"),
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, LoginInvalidJson, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message!, StringComparison.OrdinalIgnoreCase);
        auth.VerifyNoOtherCalls();   // a wrong password is not something a window can fix
    }

    [Fact]
    public async Task CheckAccount_WhenTheHostAsksForACaptcha_FallsBackToTheBrowser_AndScrapesThePasskey()
    {
        // The fallback exists because the captcha is risk-triggered, not per-login: the same request
        // succeeded minutes before it appeared. The browser path never sees the login JSON, so the
        // passkey has to come off the upload page.
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), "csuprobe", It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult("s3ss10n-from-browser", null));

        List<(string Url, IReadOnlyDictionary<string, string> Headers)> gets = [];
        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (url, headers) => { gets.Add((url, headers)); return Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])); },
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, CaptchaRequiredJson, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10n-from-browser", result.SessionCookie);
        Assert.Equal("76oecg2ydcf4gl6s", result.ApiKey);

        auth.Verify(
            a => a.AcquireSessionCookieAsync(
                It.Is<InteractiveAuthSpec>(s => s.CookieName == "autologin" && s.LoginUrl == "https://depositfiles.com/login.php"),
                "csuprobe",
                It.IsAny<ProxyChoice?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Equal("autologin=s3ss10n-from-browser", Assert.Single(gets).Headers["Cookie"]);
    }

    [Fact]
    public async Task CheckAccount_ACaptchaWithNoBrowserAvailable_SaysWhatToDo()
    {
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => throw new InvalidOperationException("nothing to fetch"),
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, CaptchaRequiredJson, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("embedded browser", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_ACancelledBrowserSignIn_IsNotAnAccount()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InteractiveAuthResult?)null);

        DepositFilesPipeline pipeline = new(
            auth.Object,
            getOverride: (_, _) => throw new InvalidOperationException("nothing to fetch"),
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, CaptchaRequiredJson, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("cancelled", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("csuprobe", "")]
    public async Task CheckAccount_WithoutBothHalves_AsksForThem_WithoutCallingTheHost(string user, string password)
    {
        DepositFilesPipeline pipeline = new(
            authService: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not fetch"),
            postFormOverride: (_, _, _) => throw new InvalidOperationException("must not post"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            user, password, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message!, StringComparison.OrdinalIgnoreCase);
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
            getOverride: (url, headers) => { gets.Add((url, headers)); return Task.FromResult(new HttpResponseSnapshot(200, UploadPageHtml, [])); },
            postFormOverride: (_, _, _) => throw new InvalidOperationException("a refresh must not need the password"));

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
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(302, string.Empty, [], "https://depositfiles.com/login.php")),
            postFormOverride: (_, _, _) => throw new InvalidOperationException("no posts"));

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
            getOverride: (_, _) => throw new HttpRequestException("no such host"),
            postFormOverride: (_, _, _) => throw new InvalidOperationException("no posts"));

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
        public Recorder() => Pipeline = new DepositFilesPipeline(null, GetAsync, PostFormAsync, UploadAsync);

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

        private Task<HttpResponseSnapshot> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string> headers)
            => throw new InvalidOperationException("an upload must not post the login form");

        private Task<HttpResponseSnapshot> UploadAsync(string filePath, string endpoint, IReadOnlyDictionary<string, string> fields)
        {
            Uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        }
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
