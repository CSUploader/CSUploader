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
    public void AccountPage_WithTheFamilyMarkupInstead_YieldsNothing()
    {
        // Guards the direction of the fix: this host must read ITS markup, not inherit a match from the
        // family's. If a future edit reverts to the base pattern, this fails rather than silently
        // returning a blank name again.
        UpZurPipeline pipeline = new();
        const string FamilyHtml = """<i class="fa fa-user"></i>someone<span class="storage"><b>1 MB</b> of <b>2 GB</b></span>""";

        Assert.Null(pipeline.ParseAccountUsernameForTests(FamilyHtml));
        Assert.Equal((null, null), pipeline.ParseStorageUsageForTests(FamilyHtml));
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

        // The credential is the xfss cookie, NOT a username/password: this host has no API, so there
        // is no key to paste and nothing for a typed password to validate against. Leaving it out of
        // this map was the reported bug — the dialog asked for a password and the pipeline then opened
        // the sign-in browser, which reads as a malfunction.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("UpZur"));
        Assert.True(HosterCredentialModes.IsWebViewSignInHoster("UpZur"));
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
