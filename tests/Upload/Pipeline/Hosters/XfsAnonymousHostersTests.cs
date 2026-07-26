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
    // Send.now homepage (live 2026-07-26) — the family's standard anonymous form.
    private const string SendNowHomeHtml = """
        <!DOCTYPE html><html><body>
        <form method="post" action="/?op=login"><input name="login"></form>
        <form id="uploadfile" action="https://dl8202.send.now/cgi-bin/upload.cgi?upload_type=file&utype=anon">
          <input type="hidden" name="sess_id" value="">
          <input type="hidden" name="utype" value="anon">
        </form>
        </body></html>
        """;

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
        Assert.Null(send.MaxFileSize); // api_get_limits reports MaxUploadFilesize=0 (unlimited)

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

    // Send.now's keyless upload-server API (live 2026-07-26). Note the ?u=api label, which the
    // pipeline replaces with the browser's own query.
    private const string SendNowApiJson =
        """{"result":"https://u0626.send.now/cgi-bin/upload.cgi?u=api","status":200,"server_time":"2026-07-26 16:10:12","msg":"OK"}""";

    [Fact]
    public async Task SendNow_Anonymous_ResolvesTheNodeFromTheKeylessApi_AndPostsTheBrowsersQuery()
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

        // Resolved from the JSON API, not the homepage.
        Assert.Equal("https://send.now/api/upload/server", Assert.Single(getUrls));

        UploadCall call = Assert.Single(calls);
        // The API's ?u=api is dropped and the browser's captured query used instead.
        Assert.Equal("https://u0626.send.now/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
        Assert.Equal("anon", call.ExtraFields["utype"]);
    }

    [Fact]
    public async Task SendNow_Anonymous_ApiUnusable_FallsBackToTheHomepageScrape()
    {
        // A WAF challenge page (or any non-JSON answer) on the API must not be fatal — the family's
        // HTML scrape is still there as a second chance.
        Queue<string> gets = new([
            "<!DOCTYPE html><html><body>Just a moment...</body></html>", // API answers with junk
            SendNowHomeHtml,                                             // homepage still has the form
        ]);
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(gets.Dequeue()),
            uploadOverride: Capture(calls, """[{"file_code":"fellback","file_status":"OK"}]"""));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

        Assert.Equal("https://send.now/fellback", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal("https://dl8202.send.now/cgi-bin/upload.cgi?upload_type=file&utype=anon", Assert.Single(calls).Endpoint);
        Assert.Empty(gets); // both sources were consulted
    }

    [Theory]
    // Real shape -> bare node (query stripped).
    [InlineData("""{"result":"https://u0626.send.now/cgi-bin/upload.cgi?u=api","msg":"OK"}""", "https://u0626.send.now/cgi-bin/upload.cgi")]
    [InlineData("""{"result":"https://dl8202.send.now/cgi-bin/upload.cgi","msg":"OK"}""", "https://dl8202.send.now/cgi-bin/upload.cgi")]
    // Unusable answers -> null so the caller falls back.
    [InlineData("""{"msg":"Invalid key","status":400}""", null)]
    [InlineData("""{"result":"","msg":"OK"}""", null)]
    [InlineData("""{"result":"https://send.now/somewhere/else","msg":"OK"}""", null)]
    [InlineData("<html>Just a moment...</html>", null)]
    [InlineData("", null)]
    public void TryReadApiUploadNode_ExtractsTheNodeOrNull(string json, string? expected)
        => Assert.Equal(expected, SendNowPipeline.TryReadApiUploadNode(json));

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
    public async Task Anonymous_FormPageWithoutAnUploadForm_YieldsAttemptFailedWithoutUpload()
    {
        List<UploadCall> calls = [];
        SendNowPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<html><body>under maintenance</body></html>"),
            uploadOverride: Capture(calls, "[]"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext("Send.now"), CancellationToken.None));

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
