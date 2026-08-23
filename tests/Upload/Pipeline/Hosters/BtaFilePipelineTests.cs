// <copyright file="BtaFilePipelineTests.cs" company="CSUploader">
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
/// BtaFile — a shim on <see cref="XFileSharingApiPipeline"/> with three deviations, each of which
/// fails quietly rather than loudly: the node comes from <c>?op=api_get_limits</c> (no anonymous form
/// is rendered anywhere), the upload form is on <c>?op=upload</c> rather than the family's
/// <c>?op=upload_form</c>, and this theme prints neither an <c>fa-user</c> name nor a
/// <c>class="storage"</c> bar. Fixtures are the real responses — the limits call and the upload
/// answer from live probes, the pages from a browser capture, with the session and account name faked.
/// </summary>
public class BtaFilePipelineTests
{
    private const string LimitsXml = """
        <Data>
        <ExtAllowed></ExtAllowed>
        <ExtNotAllowed></ExtNotAllowed>
        <MaxUploadFilesize>100</MaxUploadFilesize>
        <ServerURL>https://s200.btafile.com/cgi-bin</ServerURL>
        <SessionID></SessionID>
        <Error></Error>
        <SiteName>BtaFile</SiteName>
        <LoginLogic></LoginLogic>
        </Data>
        """;

    /// <summary>The real <c>?op=upload</c> page, trimmed to its two forms. Note BOTH actions carry the
    /// URL-importer's <c>upload_type=url</c> — including the one the file goes to.</summary>
    private const string UploadPageHtml = """
        <form id="uploadfile" action="https://s200.btafile.com/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="sess-abc">
          <input type="hidden" name="utype" value="reg">
          <input type="file" multiple id="file_0" class="uploadbtn">
        </form>
        <form method="post" id="uploadurl" action="https://s200.btafile.com/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="sess-abc">
          <input type="hidden" name="utype" value="reg">
        </form>
        """;

    /// <summary>The real signed-in <c>?op=my_files</c>, trimmed to the three things read off it. The
    /// theme has no <c>fa-user</c> icon anywhere, and its storage bar hangs off <c>id="occupied"</c>.</summary>
    private const string MyFilesHtml = """
        <a href="https://btafile.com/?op=logout">Logout</a>
        <div class="freespace">
          <span id="occupied"><b>5.0 MB</b> of <b>200.0 GB</b></span>
          <span id="files_total">1 files</span>
        </div>
        <script>
          $(input).attr('value', 'https://btafile.com/users/csuprobe/'); $(this).parent().html(input);
        </script>
        """;

    /// <summary>The real <c>/login.html</c> form. ⚠ Its <c>token</c> is genuinely empty on this host —
    /// posting the field anyway is what the browser does.</summary>
    private const string LoginPageHtml = """
        <form method="POST" action="https://btafile.com/" name="FL">
          <input type="hidden" name="op" value="login">
          <input type="hidden" name="token" value="">
          <input type="hidden" name="rand" value="">
          <input type="hidden" name="redirect" value="https://btafile.com/">
          <input type="text" name="login" value="" class="myForm">
          <input type="password" name="password" class="myForm">
        </form>
        """;

    private const string UploadOkJson = """[{"file_code":"z86hfg5iv2wd","file_status":"OK"}]""";

    // ── Anonymous ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Anonymous_TakesTheNodeFromApiGetLimits_AndBuildsTheShareLink()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(LimitsXml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://btafile.com/z86hfg5iv2wd", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The node came from the limits call — this host renders no anonymous form to scrape, on the
        // homepage or anywhere else.
        Assert.Contains("op=api_get_limits", Assert.Single(getUrls), StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://s200.btafile.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
        Assert.Equal("anon", call.ExtraFields["utype"]);
    }

    [Fact]
    public async Task RunAsync_Anonymous_TrimsATrailingSlashOffTheNode()
    {
        // ServerURL names the cgi-bin DIRECTORY, so a trailing slash would produce "//upload.cgi".
        List<UploadCall> calls = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LimitsXml.Replace("/cgi-bin", "/cgi-bin/", StringComparison.Ordinal)),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Equal("https://s200.btafile.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", Assert.Single(calls).Endpoint);
    }

    [Fact]
    public async Task RunAsync_LimitsCallWithoutAServerUrl_FailsWithoutUploading()
    {
        bool uploaded = false;
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<Data><Error>maintenance</Error></Data>"),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("ServerURL", reason, StringComparison.Ordinal);
        Assert.Contains("maintenance", reason, StringComparison.Ordinal);   // the host's own words survive
        Assert.False(uploaded);
    }

    [Fact]
    public async Task RunAsync_CloudflareChallenge_SaysSoRatherThanBlamingTheParse()
    {
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<html><title>Just a moment...</title><div id=\"cf-wrapper\"></div></html>"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Contains("Cloudflare", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FileOverTheGuestCap_IsRejectedWithoutAnyHttp()
    {
        bool touched = false;
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(LimitsXml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeAnonymousContext() with { FileSize = (100L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(touched);
    }

    [Fact]
    public async Task RunAsync_AFileAnAccountCouldTake_IsStillRefusedForAGuest()
    {
        // 100 MB guest vs 10 GB signed in is a hundredfold difference, so which cap applies is not a
        // detail — the same file is fine on one path and impossible on the other.
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LimitsXml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        AttemptContext ctx = MakeAnonymousContext() with { FileSize = 500L * 1024 * 1024 };

        Assert.Single(await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None)), e => e is AttemptFailed);
        Assert.Equal(10240L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));
    }

    // ── Signed in ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithAnAccount_ReadsTheUploadFormFromTheOpUploadPage()
    {
        // The fork quirk worth pinning: ?op=upload_form EXISTS here and returns 200 to a signed-in
        // caller — it simply renders no upload form. Pointed at it, the base scrapes nothing and
        // reports the session as expired, which is a sign-in problem the user cannot fix.
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadPageHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Contains(getUrls, u => u.EndsWith("/?op=upload", StringComparison.Ordinal));
        Assert.DoesNotContain(getUrls, u => u.Contains("op=upload_form", StringComparison.Ordinal));

        // …and never the REST API, which this host does not have (/api/* answers an HTML 404 page).
        Assert.DoesNotContain(getUrls, u => u.Contains("/api/", StringComparison.Ordinal));

        UploadCall call = Assert.Single(calls);
        Assert.Equal("sess-abc", call.ExtraFields["sess_id"]);   // XFS authenticates the upload by this ALONE
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_PostsTheScrapedActionVerbatim_UrlTypeAndAll()
    {
        // Deliberate, not an oversight: every form on this fork carries the URL-importer's
        // ?upload_type=url, including the file one. A browser capture shows a 5 MB file posted to
        // exactly that action and accepted, and a live probe confirmed the node takes a file either
        // way — so "correcting" it to upload_type=file would be changing a verified request into an
        // unverified one. (Contrast filedot.to, where the only action on the page really did belong
        // to a URL importer that would not have taken the bytes.)
        List<UploadCall> calls = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadPageHtml),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Equal("https://s200.btafile.com/cgi-bin/upload.cgi?upload_type=url", Assert.Single(calls).Endpoint);
    }

    // ── The account page ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AccountPage_YieldsTheNameAndTheStorageFigures()
    {
        BtaFilePipeline pipeline = new();

        // The name appears in exactly one place on this theme: the public-folder link the my_files
        // script builds. A path segment can only BE a name, unlike a token next to an icon (Uploady
        // saved every account as "Profile" that way).
        Assert.Equal("csuprobe", pipeline.ParseAccountUsernameForTests(MyFilesHtml));

        // Through the BASE, which learned this theme's id="occupied" bar when a second host shipped on it.
        (long? used, long? quota) = pipeline.ParseStorageUsageForTests(MyFilesHtml);
        Assert.Equal(5L << 20, used);
        Assert.Equal(200L << 30, quota);
    }

    [Fact]
    public void AccountPage_WithTheFamilyUserIconInstead_NamesNobody()
    {
        // Guards the direction of the fix: the name must come from this host's markup. What gets
        // scraped here REPLACES the stored username, and that value is what the next sign-in posts —
        // so a name picked up from whatever sits beside an icon would break signing in.
        BtaFilePipeline pipeline = new();

        Assert.Null(pipeline.ParseAccountUsernameForTests("""<i class="fa fa-user"></i>someone"""));
    }

    [Fact]
    public void AccountPage_AnotherHostsUsersPath_IsNotThisAccountsName()
    {
        // The anchor is deliberately host-qualified: a bare /users/<x> would match a link to any other
        // site's public folder that happened to be on the page.
        BtaFilePipeline pipeline = new();

        Assert.Null(pipeline.ParseAccountUsernameForTests("""<a href="https://upzur.com/users/someoneelse/">theirs</a>"""));
    }

    // ── Signing in ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAccount_SignsInByPostingTheForm_WithoutOpeningABrowser()
    {
        // authService is null, so any attempt to open the sign-in window fails the check outright —
        // which is the assertion. This host's login is a plain form with no captcha.
        List<IReadOnlyDictionary<string, string>> posts = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(url.Contains("login", StringComparison.Ordinal) ? LoginPageHtml : MyFilesHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (url, form) =>
            {
                Assert.Equal("https://btafile.com/", url);   // the form posts to the site root
                posts.Add(new Dictionary<string, string>(form));
                return Task.FromResult(new HttpResponseSnapshot(
                    302, string.Empty, ["xfss=sess-from-login; domain=.btafile.com; path=/"], "https://btafile.com/"));
            });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("sess-from-login", result.SessionCookie);
        Assert.Equal("csuprobe", result.DerivedUsername);
        Assert.Equal(5L << 20, result.StorageUsedBytes);
        Assert.Equal(200L << 30, result.StorageQuotaBytes);

        IReadOnlyDictionary<string, string> form = Assert.Single(posts);
        Assert.Equal("login", form["op"]);
        Assert.Equal("csuprobe", form["login"]);
        Assert.Equal("hunter2", form["password"]);
    }

    [Fact]
    public async Task CheckAccount_WrongPassword_FailsWithoutClaimingSuccess()
    {
        // The family gives no error envelope: a bad password re-renders the page as 200 with no
        // cookie, so "no xfss came back" is the whole signal.
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>())));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccount_WithAStoredSession_RereadsTheAccountPage_WithoutSigningInAgain()
    {
        List<string> getUrls = [];
        BtaFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(MyFilesHtml); },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a refresh"),
            postFormOverride: (_, _) => throw new InvalidOperationException("a refresh must not need the password"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            apiKey: null, sessionCookie: "xfss-stored", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss-stored", result.SessionCookie);
        Assert.Equal(5L << 20, result.StorageUsedBytes);
        Assert.Contains("op=my_files", Assert.Single(getUrls), StringComparison.Ordinal);
    }

    // ── Identity and wiring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void BtaFile_IsAnonymous_OnTheWebFormPath_WithTheCapsItsOwnApiStates()
    {
        BtaFilePipeline pipeline = new();

        Assert.Equal("BtaFile", pipeline.Name);

        // Anonymous confirmed by uploading real bytes with no account and fetching the page that came
        // back — this host renders no guest form at all, and takes the upload anyway.
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Both figures are MaxUploadFilesize from ?op=api_get_limits, asked once without a session and
        // once with the account's.
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));
        Assert.Equal(10240L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        // No REST API here, so the account path is the web form; the login is a plain form this app
        // posts itself, so no sign-in window.
        Assert.True(pipeline.UsesWebFormUploadForTests);
        Assert.True(pipeline.SupportsDirectLoginForTests);
        Assert.Equal("https://btafile.com/?op=upload", pipeline.UploadFormUrlForTests);

        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("BtaFile"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("BtaFile"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster("BtaFile"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("BtaFile"));
        Assert.Equal("btafile.com", FileHosterClient.FileHosters["BtaFile"]);
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

    private static AttemptContext MakeAnonymousContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.rar",
        FileName = "x.rar",
        FileSize = 4096,
        HosterName = "BtaFile",
        Credentials = new FileHosterLoginDto { FileHosterName = "BtaFile", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private static AttemptContext MakeAccountContext() => MakeAnonymousContext() with
    {
        Credentials = new FileHosterLoginDto
        {
            Id = 3,
            FileHosterName = "BtaFile",
            IsAnonymous = false,
            Username = "csuprobe",
            SessionCookie = "xfss-value",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
            PinnedProxyId = null,   // unpinned → valid against any proxy, so no sign-in window
        },
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);
}
