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
/// The anonymous half of the 2026-07-26 XFileSharing batch — Send.now, plus the retained-but-disabled
/// DropGalaxy — as thin shims on <see cref="XFileSharingApiPipeline"/>. The fixtures below are the
/// real shapes captured from each live site, so these pin what a shim can get wrong: WHERE the
/// anonymous upload node is discovered, and how the shared anonymous retry rules behave.
/// <para>
/// Uploady was part of that batch but is no longer anonymous — its guest path is broken server-side —
/// so it lives in <see cref="UploadyPipelineTests"/> on the web-form path.
/// </para>
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
    }

    [Fact]
    public void SendNow_IsRegisteredWithADomainAndTheApiKeyCredentialMode()
    {
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Send.now"), "Send.now missing from the hoster registry");
        Assert.False(string.IsNullOrWhiteSpace(FileHosterClient.FileHosters["Send.now"]), "Send.now has no domain");
        // Accounts use the family's standard WebView-sign-in -> API-key flow, like most of its siblings.
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("Send.now"));
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

    // Send.now's /api/upload/server - the ONLY path Cloudflare lets through (every ?op=... page,
    // including api_get_limits, answers a real client with a managed-challenge 403).
    private const string SendNowApiJson =
        """{"result":"https://u9750.send.now/cgi-bin/upload.cgi?u=api","status":200,"server_time":"2026-07-27 15:05:03","msg":"OK"}""";

    // The lockout the keyless /api/upload/server hands out once it has counted enough anonymous calls
    // as failed authentications. HTTP 200 with the refusal in the envelope.
    private const string SendNowLockoutJson =
        """{"server_time":"2026-07-27 15:02:58","msg":"Too many failed attempts. Please try again in 60 minutes.","status":429}""";

    [Fact]
    public async Task SendNow_Anonymous_ResolvesTheNodeFromTheApi_AndPostsTheBrowsersQuery()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowApiJson); },
            uploadOverride: Capture(calls, """[{"file_code":"abc123xyz","file_status":"OK"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://send.now/abc123xyz", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // /api/* is the only route that is not Cloudflare-challenged, so it is the only option - and it
        // must be called rarely, hence the caching covered below.
        Assert.Equal("https://send.now/api/upload/server", Assert.Single(getUrls));

        UploadCall call = Assert.Single(calls);
        // The API's ?u=api label is dropped and the browser's captured query used instead.
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
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowApiJson); },
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
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowApiJson); },
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
        // The real lockout envelope: HTTP 200 carrying status 429. Its message is quoted back verbatim
        // (it says how long to wait), and exactly ONE request is made - no fallback to a page path,
        // every one of which is Cloudflare-challenged.
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
        Assert.Equal("https://send.now/api/upload/server", Assert.Single(getUrls));
        Assert.Empty(calls);
    }

    [Theory]
    // Real shape -> the node, query stripped.
    [InlineData("""{"result":"https://u0626.send.now/cgi-bin/upload.cgi?u=api","msg":"OK"}""", "https://u0626.send.now/cgi-bin/upload.cgi")]
    [InlineData("""{"result":"https://dl8202.send.now/cgi-bin/upload.cgi","msg":"OK"}""", "https://dl8202.send.now/cgi-bin/upload.cgi")]
    // Unusable answers -> null so the caller reports a clear failure.
    [InlineData("""{"status":429,"msg":"Too many failed attempts. Please try again in 60 minutes."}""", null)]
    [InlineData("""{"result":"","msg":"OK"}""", null)]
    [InlineData("""{"result":"https://send.now/somewhere/else","msg":"OK"}""", null)]
    [InlineData("<html><title>Just a moment...</title></html>", null)]
    [InlineData("", null)]
    public void TryReadApiUploadNode_ExtractsTheNodeOrNull(string json, string? expected)
        => Assert.Equal(expected, SendNowPipeline.TryReadApiUploadNode(json));

    [Theory]
    [InlineData("""{"status":429,"msg":"Too many failed attempts. Please try again in 60 minutes."}""", "Too many failed attempts. Please try again in 60 minutes.")]
    [InlineData("""{"msg":"OK","result":"x"}""", "OK")]
    [InlineData("""{"status":500}""", null)]
    [InlineData("<html>not json</html>", null)]
    public void TryReadApiMessage_SurfacesTheHostsOwnWords(string json, string? expected)
        => Assert.Equal(expected, SendNowPipeline.TryReadApiMessage(json));

    [Fact]
    public async Task Anonymous_NodeBackendFailure_RetriesOnceWithAFreshNode()
    {
        // Seen live on this family: the node accepted the bytes, then its own storage CGI died and the
        // failure came back inside file_status. That is a bad draw from the rotating pool, not a
        // verdict on the file, so a fresh node gets one more go.
        const string NodeBroke =
            """[{"file_code":"undef","file_status":"failed while requesting fs.cgi: <html><title>500 Internal Server Error</title></html>"}]""";

        List<string> getUrls = [];
        int uploadCalls = 0;
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(SendNowApiJson); },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult(new HttpResponseSnapshot(
                    200,
                    uploadCalls == 1 ? NodeBroke : """[{"file_code":"second","file_status":"OK"}]""",
                    Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        Assert.Equal("https://send.now/second", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, uploadCalls);      // re-sent once
        Assert.Equal(2, getUrls.Count);    // ...to a freshly resolved node
        Assert.Single(events.OfType<TransferStarted>()); // one transfer from the UI's point of view
    }

    [Fact]
    public async Task Anonymous_NodeBackendFailurePersists_FailsAfterExactlyOneRetry()
    {
        // The retry re-sends the whole file, so it must never become a loop.
        const string NodeBroke =
            """[{"file_code":"undef","file_status":"failed while requesting fs.cgi: 500 Internal Server Error"}]""";

        int uploadCalls = 0;
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(SendNowApiJson),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult(new HttpResponseSnapshot(200, NodeBroke, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("fs.cgi", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, uploadCalls); // the original send + exactly one retry
    }

    [Fact]
    public async Task Anonymous_FileRejected_IsNeverReUploaded()
    {
        // The counterpart guard: a verdict on the FILE must not be retried, or a too-big file is sent
        // twice to be refused twice.
        int uploadCalls = 0;
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(SendNowApiJson),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"undef","file_status":"File too big"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        Assert.Contains("File too big", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Equal(1, uploadCalls); // sent once, never again
    }

    [Fact]
    public void SendNow_ConcurrencyCap_IsReachableThroughTheInterface()
    {
        // Regression guard: MaxConcurrentUploadsFor has a DEFAULT implementation on the interface, so
        // if the class's version is ever made static (a linter will suggest exactly that, since it
        // touches no instance state) it stops implementing the member and the cap silently reverts to
        // "no limit". Asserting through the interface is what catches that.
        IFileHosterPipeline pipeline = new SendNowPipeline();
        Assert.Equal(4, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { IsAnonymous = true }));
    }

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
        // Hexload (not Send.now) exercises the base's HTML-scraping path — Send.now resolves its node
        // from the JSON API and never fetches a page.
        List<UploadCall> calls = [];
        HexloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<html><body>under maintenance</body></html>"),
            uploadOverride: Capture(calls, "[]"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Hexload"), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls);
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
