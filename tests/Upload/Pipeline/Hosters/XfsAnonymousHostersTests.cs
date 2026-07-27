// <copyright file="XfsAnonymousHostersTests.cs" company="CSUploader">
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
using Xunit;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// The 2026-07-26 XFileSharing batch — Send.now, DropGalaxy, Uploady — all thin shims on
/// <see cref="XFileSharingApiPipeline"/>. The HTML fixtures below are the real shapes captured from
/// each live site, so these pin the two things a shim can get wrong: WHERE the anonymous form is
/// scraped from, and WHICH form is picked when a page carries more than one <c>upload.cgi</c> form.
/// </summary>
public class XfsAnonymousHostersTests
{
    // DropGalaxy homepage (live 2026-07-26) — same shape, but the upload node is on a DIFFERENT domain.
    private const string DropGalaxyHomeHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" action="https://dg.a2zupload.com/cgi-bin/upload.cgi?upload_type=file&utype=anon">
          <input type="hidden" name="sess_id" value="">
        </form>
        </body></html>
        """;

    // Uploady ?op=upload_form (live 2026-07-26) — TWO upload.cgi forms: the file uploader first, then
    // the remote/URL uploader posting to the same path WITHOUT the query. Its homepage has neither.
    private const string UploadyFormPageHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" action="https://lsw2.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&utype=anon">
          <input type="hidden" name="sess_id" value="">
        </form>
        <form method="post" id="uploadurl" action="https://s2.gamezizo.com/cgi-bin/upload.cgi">
          <input type="text" name="url_mass">
        </form>
        </body></html>
        """;

    [Fact]
    public void Properties_DeclareEachHostersConfig()
    {
        SendNowPipeline send = new();
        Assert.Equal("Send.now", send.Name);
        Assert.True(send.SupportsAnonymousUpload);
        // Tiers: guests 100 GB, any signed-in account unlimited (api_get_limits: MaxUploadFilesize=0).
        Assert.Null(send.MaxFileSize);
        Assert.Equal(100L * 1000 * 1000 * 1000, send.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));
        Assert.Null(send.MaxFileSizeFor(new FileHosterLoginDto { Username = "u", Password = "p" }));
        Assert.Null(send.MaxFileSizeFor(new FileHosterLoginDto { ApiKey = "k" }));

        UploadyPipeline uploady = new();
        Assert.Equal("Uploady", uploady.Name);
        Assert.True(uploady.SupportsAnonymousUpload);
        Assert.Equal(5120L * 1024 * 1024, uploady.MaxFileSize); // api_get_limits: 5120 MB
    }

    [Fact]
    public void EachHoster_IsRegisteredWithADomainAndTheApiKeyCredentialMode()
    {
        foreach (string name in new[] { "Send.now", "Uploady" })
        {
            Assert.True(FileHosterClient.FileHosters.ContainsKey(name), $"{name} missing from the hoster registry");
            Assert.False(string.IsNullOrWhiteSpace(FileHosterClient.FileHosters[name]), $"{name} has no domain");
            // Accounts use the family's standard WebView-sign-in -> API-key flow, like their siblings.
            Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode(name));
        }
    }

    [Fact]
    public void DropGalaxy_IsDisabled_AndStaysOutOfTheRegistryAndCredentialModes()
    {
        // DISABLED 2026-07-26, the day it was added: anonymous uploads cap at 0.00001 MB (~10 bytes —
        // the host answers "File size limit is 0.00001 Mbytes") and registration is closed, so the
        // API-key path is unreachable too. The pipeline class is retained (its protocol wiring is
        // correct and live-verified) but must not be offerable. Flipping this test is step 4 of the
        // re-enable checklist in DropGalaxyPipeline.cs.
        DropGalaxyPipeline pipeline = new();
        Assert.Equal("DropGalaxy", pipeline.Name);
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode(pipeline.Name)); // i.e. not ApiKey
    }

    // Send.now's ?op=api_get_limits (live 2026-07-27). ServerURL is the node DIRECTORY, no script name.
    private const string SendNowLimitsXml =
        """<Data><ExtAllowed></ExtAllowed><MaxUploadFilesize>0</MaxUploadFilesize><ServerURL>https://u9750.send.now/cgi-bin</ServerURL><SessionID></SessionID><SiteName>Send.now</SiteName></Data>""";

    // The lockout the keyless /api/upload/server hands out once it has counted enough anonymous calls
    // as failed authentications. HTTP 200 with the refusal in the envelope.
    private const string SendNowLockoutJson =
        """{"server_time":"2026-07-27 15:02:58","msg":"Too many failed attempts. Please try again in 60 minutes.","status":429}""";

    [Fact]
    public async Task SendNow_Anonymous_ResolvesTheNodeFromApiGetLimits_AndPostsTheBrowsersQuery()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowLimitsXml); },
            uploadOverride: Capture(calls, """[{"file_code":"abc123xyz","file_status":"OK"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://send.now/abc123xyz", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The session/limits endpoint - NOT the homepage (Cloudflare-challenged) and NOT the keyless
        // /api/upload/server (which answers a while, then locks the IP out for an hour).
        Assert.Equal("https://send.now/?op=api_get_limits", Assert.Single(getUrls));

        UploadCall call = Assert.Single(calls);
        // ServerURL + the script + the browser's captured query.
        Assert.Equal("https://u9750.send.now/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
        Assert.Equal("anon", call.ExtraFields["utype"]);
    }

    [Fact]
    public async Task SendNow_CachesTheNode_SoAPackageCostsOneLookupNotOnePerFile()
    {
        // The regression this prevents: every queued file performed its own lookup, and Send.now treats
        // a burst of anonymous lookups as abuse - one package earned a 60-minute lockout.
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowLimitsXml); },
            uploadOverride: Capture(calls, """[{"file_code":"ok","file_status":"OK"}]"""));

        // Three files through the SAME pipeline instance (it is a DI singleton in production).
        for (int i = 0; i < 3; i++)
        {
            await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));
        }

        Assert.Equal(3, calls.Count);   // all three uploaded
        Assert.Single(getUrls);         // ...from ONE node lookup
    }

    [Fact]
    public async Task SendNow_DeadNode_RefetchesAFreshNodeForTheRetry()
    {
        // Caching must not defeat the rotating-node retry: when the node it handed out is unreachable,
        // the same attempt comes back and must be given a freshly looked-up node.
        List<string> getUrls = [];
        int uploadCalls = 0;
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowLimitsXml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return uploadCalls == 1
                    ? Task.FromException<HttpResponseSnapshot>(new HttpRequestException(HttpRequestError.NameResolutionError, "dead node"))
                    : Task.FromResult(new HttpResponseSnapshot(200, """[{"file_code":"retried","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        Assert.Equal("https://send.now/retried", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(2, uploadCalls);
        Assert.Equal(2, getUrls.Count); // the cache was dropped and a fresh node fetched
    }

    [Fact]
    public async Task SendNow_GuestFileOverTheHundredGbCap_IsRejectedWithoutAnyHttp()
    {
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not fetch"),
            uploadOverride: Capture(calls, "[]"));

        AttemptContext ctx = MakeAnonymousContext("Send.now") with { FileSize = (100L * 1000 * 1000 * 1000) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task SendNow_LookupRefused_ReportsTheHostsOwnMessage_AndTouchesNothingElse()
    {
        // The real 429 lockout envelope. Whatever the lookup answers, exactly ONE request is made and
        // it is the limits endpoint - no fallback to the Cloudflare-challenged homepage, and never a
        // call to the keyless API that hands out the lockout in the first place.
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowLockoutJson); },
            uploadOverride: Capture(calls, "[]"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Too many failed attempts", fail.Reason, StringComparison.Ordinal); // the host's words reach the user
        Assert.Equal("https://send.now/?op=api_get_limits", Assert.Single(getUrls));
        Assert.Empty(calls);
    }

    [Theory]
    // Real shape -> the node directory, verbatim.
    [InlineData("<Data><ServerURL>https://u9750.send.now/cgi-bin</ServerURL></Data>", "https://u9750.send.now/cgi-bin")]
    [InlineData("<Data><ServerURL>  https://u0626.send.now/cgi-bin  </ServerURL></Data>", "https://u0626.send.now/cgi-bin")]
    // Unusable answers -> null so the caller reports a clear failure.
    [InlineData("<Data><ServerURL></ServerURL></Data>", null)]
    [InlineData("<Data><SessionID></SessionID></Data>", null)]
    [InlineData("""{"status":429,"msg":"Too many failed attempts."}""", null)]
    [InlineData("<html><title>Just a moment...</title></html>", null)]
    [InlineData("", null)]
    public void TryReadServerUrl_ExtractsTheNodeDirectoryOrNull(string body, string? expected)
        => Assert.Equal(expected, SendNowPipeline.TryReadServerUrl(body));

    /// <summary>Kept while DropGalaxy is disabled: it pins the (correct, live-verified) protocol
    /// wiring so a re-enable — should the cap ever become usable — starts from a known-good shim.</summary>
    [Fact]
    public async Task DropGalaxy_Anonymous_PostsToTheSeparateUploadDomainFromTheForm()
    {
        List<UploadCall> calls = [];
        DropGalaxyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(DropGalaxyHomeHtml),
            uploadOverride: Capture(calls, """[{"file_code":"dg9kk2","file_status":"OK"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("DropGalaxy"), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        // The share link is built from the HOST, even though the bytes went to a2zupload.com.
        Assert.Equal("https://dropgalaxy.com/dg9kk2", tc.FileUrl);
        Assert.Equal("https://dg.a2zupload.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", Assert.Single(calls).Endpoint);
    }

    [Fact]
    public async Task Uploady_Anonymous_ScrapesTheUploadFormPage_AndPicksTheFileFormNotTheUrlForm()
    {
        // Both halves of Uploady's deviation in one assertion set: its homepage carries no form at
        // all (so the scrape must target ?op=upload_form), and that page carries a SECOND upload.cgi
        // form (the remote/URL uploader) that must not be chosen.
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadyFormPageHtml); },
            uploadOverride: Capture(calls, """[{"file_code":"upl77","file_status":"OK"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Uploady"), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://uploady.io/upl77", tc.FileUrl);

        string url = Assert.Single(getUrls);
        Assert.Contains("op=upload_form", url, StringComparison.Ordinal); // NOT the homepage
        Assert.Contains("&_=", url, StringComparison.Ordinal);            // still cache-busted

        // The file uploader (with the utype=anon query), never the url_mass form.
        Assert.Equal("https://lsw2.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", Assert.Single(calls).Endpoint);
    }

    [Fact]
    public async Task Uploady_AnonymousRetries_RefetchTheFormPageWithDistinctCacheBusters()
    {
        // The rotating-node retry must keep working through the overridden form URL.
        List<string> getUrls = [];
        int uploadCalls = 0;
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadyFormPageHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return uploadCalls < 2
                    ? Task.FromException<HttpResponseSnapshot>(new HttpRequestException(HttpRequestError.NameResolutionError, "dead node"))
                    : Task.FromResult(new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Uploady"), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(2, getUrls.Count);
        Assert.All(getUrls, u => Assert.Contains("op=upload_form", u, StringComparison.Ordinal));
        Assert.NotEqual(getUrls[0], getUrls[1]);
    }

    [Fact]
    public async Task Anonymous_CloudflareChallengeInterstitial_SaysSoInsteadOfFormNotFound()
    {
        // A real user hit this: send.now answered 403 + Cf-Mitigated: challenge with the "Just a
        // moment..." page. That is a wall this app cannot pass (a managed challenge validates the
        // client itself), so the message must name it rather than report a parse failure.
        const string ChallengeHtml = """
            <!DOCTYPE html><html lang="en-US"><head><title>Just a moment...</title></head>
            <body><script>window._cf_chl_opt={cType:'managed',cZone:'send.now'};</script>
            <script src="/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1"></script></body></html>
            """;
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(ChallengeHtml),
            uploadOverride: Capture(calls, "[]"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Cloudflare", fail.Reason, StringComparison.Ordinal);
        Assert.Contains("challenge", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(calls); // no bytes sent
    }

    [Fact]
    public async Task Anonymous_FormPageWithoutAnUploadForm_YieldsAttemptFailedWithoutUpload()
    {
        // Uploady (not Send.now) exercises the base's HTML-scraping path — Send.now resolves its node
        // from the JSON API and never fetches a page.
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<html><body>under maintenance</body></html>"),
            uploadOverride: Capture(calls, "[]"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Uploady"), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Anonymous_ServerRejectsTheFile_SurfacesTheHostersReason()
    {
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadyFormPageHtml),
            uploadOverride: Capture(calls, """[{"file_code":"","file_status":"File too big"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Uploady"), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("File too big", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task Uploady_FileOverTheFiveGbCap_IsRejectedWithoutAnyHttp()
    {
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not fetch"),
            uploadOverride: Capture(calls, "[]"));

        AttemptContext ctx = MakeAnonymousContext("Uploady") with { FileSize = (5120L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    private static Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> Capture(
        List<UploadCall> calls, string responseBody)
        => (filePath, endpoint, fields, headers, _) =>
        {
            calls.Add(new UploadCall(
                filePath,
                endpoint,
                new Dictionary<string, string>(fields),
                headers is null ? null : new Dictionary<string, string>(headers)));
            return Task.FromResult(new HttpResponseSnapshot(200, responseBody, Array.Empty<string>()));
        };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeAnonymousContext(string hoster) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_225_142,
        HosterName = hoster,
        Credentials = new FileHosterLoginDto { FileHosterName = hoster, IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
