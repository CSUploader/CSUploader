// <copyright file="UpZurPipelineTests.cs" company="CSUploader">
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
/// UpZur — an anonymous shim on <see cref="XFileSharingApiPipeline"/> whose one deviation is where the
/// upload node comes from: its homepage renders NO upload form, so the base's scrape would find
/// nothing and the node is read from <c>?op=api_get_limits</c> instead. Fixture is that call's real
/// response (2026-08-06).
/// </summary>
public class UpZurPipelineTests
{
    private const string LimitsXml = """
        <Data>
        <ExtAllowed></ExtAllowed>
        <ExtNotAllowed></ExtNotAllowed>
        <MaxUploadFilesize>200</MaxUploadFilesize>
        <ServerURL>https://systeme.upzur.com/cgi-bin</ServerURL>
        <SessionID></SessionID>
        <Error></Error>
        <SiteName>UpZur</SiteName>
        <LoginLogic></LoginLogic>
        </Data>
        """;

    [Fact]
    public async Task RunAsync_Anonymous_TakesTheNodeFromApiGetLimits_AndBuildsTheShareLink()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(LimitsXml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"a60qwb7n8p6n","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://upzur.com/a60qwb7n8p6n", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The node came from the limits call — NOT from a scraped form, which this host doesn't render.
        Assert.Contains("op=api_get_limits", Assert.Single(getUrls), StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal(
            "https://systeme.upzur.com/cgi-bin/upload.cgi?upload_type=file&utype=anon",
            call.Endpoint);

        // The family's anonymous field set, byte-for-byte what the live probe sent.
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
        Assert.Equal("anon", call.ExtraFields["utype"]);
    }

    [Fact]
    public async Task RunAsync_LimitsCallWithoutAServerUrl_FailsWithoutUploading()
    {
        // A WAF page, a template change, or a maintenance stub: whatever it is, uploading into it
        // would only produce a confusing failure, and the snippet is what tells them apart.
        bool uploaded = false;
        UpZurPipeline pipeline = new(
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
        Assert.Contains("maintenance", reason, StringComparison.Ordinal); // the host's own words survive
        Assert.False(uploaded);
    }

    [Fact]
    public async Task RunAsync_CloudflareChallenge_SaysSoRatherThanBlamingTheParse()
    {
        // Same wall TakeFile hit. A managed challenge validates the browser itself, so "no ServerURL
        // found" would send the user hunting for a parsing bug that isn't there.
        UpZurPipeline pipeline = new(
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
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(LimitsXml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeAnonymousContext() with { FileSize = (200L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(touched);
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_UsesTheWebFormPath_NotTheApiKeyPath()
    {
        // This host has NO API — /api/upload/server 404s and /api/account/info answers a 500 HTML
        // error page. So a signed-in attempt must scrape ?op=upload_form for the node and sess_id.
        // Before this, the base's username/password path opened the sign-in browser and then hunted
        // my_account for an API key that is never rendered: the check failed after a good sign-in,
        // which reads to the user like a wrong password.
        List<string> getUrls = [];
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) =>
            {
                getUrls.Add(url);
                return Task.FromResult("""
                    <form id="uploadfile" action="https://systeme.upzur.com/cgi-bin/upload.cgi?upload_type=file&utype=reg">
                      <input type="hidden" name="sess_id" value="sess-abc">
                    </form>
                    """);
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """[{"file_code":"a60qwb7n8p6n","file_status":"OK"}]""", Array.Empty<string>())));

        AttemptContext ctx = MakeAnonymousContext() with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 3,
                FileHosterName = "UpZur",
                IsAnonymous = false,
                Username = "someone",
                SessionCookie = "xfss-value",
                SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
                PinnedProxyId = null, // unpinned → valid against any proxy, so no sign-in window
            },
        };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        // The upload form page — never /api/upload/server, which this host doesn't have.
        Assert.Contains(getUrls, u => u.Contains("op=upload_form", StringComparison.Ordinal));
        Assert.DoesNotContain(getUrls, u => u.Contains("/api/", StringComparison.Ordinal));
    }

    // The account page's real markup (?op=my_files, signed in, 2026-08-07), trimmed to the two things
    // read off it. Both differ from the family's, and both returned nothing before this: the theme has
    // NO fa-user icon anywhere, and its storage bar hangs off id="occupied", not class="storage".
    private const string MyFilesHtml = """
        <a href="https://upzur.com/?op=logout">Logout</a>
        <div class="freespace">
          <span id="occupied"><b>0 MB</b> of <b>1953.1 GB</b></span>
          <span id="files_total">0 Files</span>
        </div>
        <script>
          $(input).attr('value', 'https://upzur.com/users/csuprobe/'); $(this).parent().html(input);
        </script>
        """;

    [Fact]
    public void AccountPage_YieldsTheNameAndTheStorageFigures()
    {
        UpZurPipeline pipeline = new();

        // Was blank in the Add Account dialog: it said "Signed in" and named nobody, because the base
        // anchors on an fa-user icon this theme doesn't have. The /users/<name>/ path is the only place
        // the name appears — and a path segment can only BE a name, unlike a token next to an icon
        // (Uploady saved every account as "Profile" that way; EliteFile as "Settings").
        Assert.Equal("csuprobe", pipeline.ParseAccountUsernameForTests(MyFilesHtml));

        (long? used, long? quota) = pipeline.ParseStorageUsageForTests(MyFilesHtml);
        Assert.Equal(0L, used);
        Assert.Equal((long)(1953.1 * (1L << 30)), quota);
    }

    [Fact]
    public void AccountPage_WithTheFamilyMarkupInstead_NamesNobody()
    {
        // Guards the direction of the fix: the NAME must come from this host's markup, not from a
        // family anchor that would hand back whatever sits beside an icon. If a future edit reverts to
        // the base pattern, this fails rather than silently saving a wrong name — and a wrong name here
        // is the login identifier, so it would break the next sign-in.
        UpZurPipeline pipeline = new();
        const string FamilyHtml = """<i class="fa fa-user"></i>someone<span class="storage"><b>1 MB</b> of <b>2 GB</b></span>""";

        Assert.Null(pipeline.ParseAccountUsernameForTests(FamilyHtml));

        // The STORAGE bar is a different case, and deliberately so: since BtaFile turned up on this
        // same theme, the base understands both anchors, so the family bar parses here too. A figure
        // read from the wrong bar costs a display number; a name read from the wrong place costs the
        // account.
        Assert.Equal((1L << 20, 2L << 30), pipeline.ParseStorageUsageForTests(FamilyHtml));
    }

    [Fact]
    public async Task RefreshAccount_WithAStoredSession_RereadsTheAccountPage_WithoutSigningInAgain()
    {
        // The reported bug: sign in, press Save, and the sign-in window opens a SECOND time. Saving runs
        // a verification pass, and until the base implemented the refresh contract that pass had no way
        // to say "I already have a session" — so it re-ran the interactive check. authService is null
        // here, so anything that tried to open a sign-in window would fail the test rather than pass it.
        List<string> getUrls = [];
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(MyFilesHtml); },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a refresh"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            apiKey: null,
            sessionCookie: "xfss-stored",
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("csuprobe", result.DerivedUsername);
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Equal("xfss-stored", result.SessionCookie); // the stored session survives the round trip

        // It read the account page — the one page this needs — and nothing else.
        Assert.Contains("op=my_files", Assert.Single(getUrls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAccount_WhenTheStoredSessionHasLapsed_KeepsTheAccountValid()
    {
        // What a lapsed cookie MEANS depends on what the credential is. Here a username and password
        // are stored and the app can sign in again by itself, so the account is fine and the next
        // upload just re-logs-in — reporting a failure would auto-disable a working account over an
        // expiry the user can neither see nor act on. (A cookie-ONLY hoster reports invalid instead;
        // there the cookie is all there is.)
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<html><body><a href=\"/?op=login\">Login</a></body></html>"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a refresh"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            apiKey: null,
            sessionCookie: "xfss-expired",
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task CheckAccount_SignsInByPostingTheForm_WithoutOpeningABrowser()
    {
        // authService is null, so ANY attempt to open the sign-in window fails the check outright —
        // which is the assertion. The login is a plain form behind only passive Cloudflare, so the
        // browser bought nothing here except a second thing for the user to do.
        List<IReadOnlyDictionary<string, string>> posts = [];
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(url.Contains("op=login", StringComparison.Ordinal)
                ? """<form method="POST" action="https://upzur.com/"><input type="hidden" name="token" value="tok123"><input name="login"><input name="password"></form>"""
                : MyFilesHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (url, form) =>
            {
                Assert.Equal("https://upzur.com/", url); // the form posts to the site root
                posts.Add(new Dictionary<string, string>(form));
                return Task.FromResult(new HttpResponseSnapshot(
                    302, string.Empty, ["xfss=sess-from-login; path=/"], "https://upzur.com/?op=my_files"));
            });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null,
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("csuprobe", result.DerivedUsername);
        Assert.Equal(0L, result.StorageUsedBytes);

        IReadOnlyDictionary<string, string> form = Assert.Single(posts);
        Assert.Equal("login", form["op"]);
        Assert.Equal("csuprobe", form["login"]);
        Assert.Equal("hunter2", form["password"]);
        Assert.Equal("tok123", form["token"]); // the anti-CSRF token echoed back off the login page
    }

    [Fact]
    public async Task CheckAccount_WrongPassword_FailsWithoutClaimingSuccess()
    {
        // The family gives no error envelope: a bad password re-renders the page as 200 with no
        // cookie, so "no xfss came back" is the whole signal.
        UpZurPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("""<input type="hidden" name="token" value="tok123">"""),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(
                new HttpResponseSnapshot(200, "<html>Login</html>", Array.Empty<string>())));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "wrong", null,
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignInOpensTheOpRoute_NotTheFamilyDefaultLoginPage()
    {
        // The reported bug: the sign-in window opened the front page with nothing to sign in with.
        // /login.html does not exist here — it bounces 301 -> /login -> 302 -> the homepage. The login
        // form lives on the op route, which is where the window must land.
        Assert.Equal("https://upzur.com/?op=login", new UpZurPipeline().SignInPageUrlForTests);
    }

    [Fact]
    public void UpZur_IsAnonymous_WithTheCapItsOwnApiStates()
    {
        UpZurPipeline pipeline = new();
        Assert.Equal("UpZur", pipeline.Name);

        // Anonymous confirmed by an actual upload whose link resolves — the candidate list that
        // suggested this host had it filed "Sign-Up Required".
        Assert.True(pipeline.SupportsAnonymousUpload);

        // 200 MB is MaxUploadFilesize from the keyless limits call, i.e. the guest figure. The list
        // advertised "5GB / 1.95TB"; those are the paid tiers.
        Assert.Equal(200L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));

        // Username and password, and NO sign-in window: this host has no API (so no key to paste) but
        // its login is a plain form with no captcha behind only passive Cloudflare, which this app can
        // post itself. Being keyless is not what puts a hoster in the session-cookie family — needing
        // a human to fetch the cookie is.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("UpZur"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("UpZur"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster("UpZur"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("UpZur"));
        Assert.Equal("upzur.com", FileHosterClient.FileHosters["UpZur"]);
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

    private static AttemptContext MakeAnonymousContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.rar",
        FileName = "x.rar",
        FileSize = 4096,
        HosterName = "UpZur",
        Credentials = new FileHosterLoginDto { FileHosterName = "UpZur", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);
}
