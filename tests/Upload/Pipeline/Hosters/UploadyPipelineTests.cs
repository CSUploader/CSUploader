// <copyright file="UploadyPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// Uploady on the web-form (no-API) path — the sibling of <see cref="IsraCloudPipelineTests"/>, and
/// the regression net for two things this hoster gets wrong in its own way: its
/// <c>?op=upload_form</c> carries THREE <c>upload.cgi</c> forms (only the first is ours), and its
/// storage figures live on a re-skinned <c>?op=my_account</c> dashboard rather than the family's
/// <c>?op=my_files</c> bar. Fixtures are the shapes from the browser capture 2026-07-27, session
/// values redacted.
/// </summary>
public class UploadyPipelineTests
{
    // The logged-in ?op=upload_form page, in the live document order. Everything after the first form
    // is decoy: the remote-URL uploader and the torrent dropbox BOTH post to an upload.cgi (the latter
    // on a different node entirely), so a scrape that didn't stop at the first would send the file to
    // the wrong endpoint.
    private const string UploadFormHtml = """
        <!doctype html><html><body>
        <form id="uploadfile" action="https://s5.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&utype=reg&ptype=ppd">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
          <input type="file" multiple="multiple" id="file_0">
        </form>
        <form method="post" id="uploadurl" action="https://s5.gamezizo.com/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
          <textarea name="url_mass"></textarea>
        </form>
        <form method="post" action="" onsubmit="return checkTos();">
          <input type="hidden" name="op" value="my_files">
        </form>
        <form action="https://s2.gamezizo.com/cgi-bin/upload.cgi" method="POST" enctype="multipart/form-data">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="torr_on" value="1">
        </form>
        </body></html>
        """;

    // The logged-in ?op=my_account dashboard. Note the decoys: SEVEN dash-stat-value cells, two of
    // which hold a size — and the one that isn't storage ("Bandwidth Usage … 10.00 GB remaining") sits
    // right next to the one that is. That is why both scrapes anchor on their own label.
    private const string MyAccountHtml = """
        <!doctype html><html><body>
        <a href="/?op=logout" class="btn">Logout</a>
        <div class="dash-stat"><div class="dash-stat-label">Available Funds</div>
          <div class="dash-stat-value">$0.00</div></div>
        <div class="dash-stat">
          <div class="d-flex justify-content-between align-items-start mb-3">
            <div>
              <div class="dash-stat-label">Storage Usage</div>
              <div class="dash-stat-value">1000.00 <small>GB total</small></div>
            </div>
            <div class="dash-icon"><i class="fal fa-hdd"></i></div>
          </div>
          <div class="dash-progress-meta">
            <span><i class="fal fa-database mr-1"></i>Space used</span> <span>0.00 MB</span>
          </div>
        </div>
        <div class="dash-stat"><div class="dash-stat-label">Bandwidth Usage</div>
          <div class="dash-stat-value">10.00 <small>GB remaining</small></div></div>
        <div class="dash-api"><h4>No API Key Found</h4>
          <a href="/?op=my_account&generate_api_key=1&token=tok" class="dash-save-btn">Generate API Key</a></div>
        </body></html>
        """;

    // What any logged-in page serves once the cookie stops authenticating us: the login page.
    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Sign in</title></head><body>
        <form action="/login.html" method="post"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    private const long Quota1000Gb = 1000L * 1024 * 1024 * 1024; // "1000.00 GB", binary units

    [Fact]
    public void Uploady_IsAccountOnly_OnTheWebFormPath_WithTheRegisteredTierCap()
    {
        UploadyPipeline pipeline = new();
        Assert.Equal("Uploady", pipeline.Name);

        // 10 GB — the cap the LOGGED-IN upload page states (max_upload_filesize: '10240' MB). The guest
        // form advertises 5120 MB, but guests can't upload here at all, so that figure never applies.
        Assert.Equal(10240L * 1024 * 1024, pipeline.MaxFileSize);

        // Registered and offerable, with the session cookie as its only credential — Uploady mints an
        // API key only on request ("No API Key Found"), so there is nothing to paste.
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Uploady"));
        Assert.Equal("uploady.io", FileHosterClient.FileHosters["Uploady"]);
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("Uploady"));
    }

    [Fact]
    public void Uploady_DoesNotOfferAnonymousUpload_BecauseTheHostItselfFailsGuestUploads()
    {
        // Established 2026-07-27 and NOT a client-side problem: a Fiddler capture of Firefox doing the
        // guest upload got [{"file_code":"undef","file_status":"failed while requesting fs.cgi: <500
        // Internal Server Error>"}], while the same capture's registered upload — same file, same node
        // (s5.gamezizo.com), minutes apart — returned file_status OK. Guests can reach the form and
        // spend the whole upload; the host just never stores the result. Re-enable only on a fresh
        // capture showing a guest upload actually succeeding.
        Assert.False(new UploadyPipeline().SupportsAnonymousUpload);
    }

    [Fact]
    public async Task RunAsync_WebForm_PicksTheFirstUploadForm_AndPostsTheCapturedFieldSet()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadFormHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"07mj6cc3wyee","file_status":"OK"}]""", Array.Empty<string>()));
            });

        // A valid stored session cookie → no WebView; the upload server comes from ?op=upload_form.
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://uploady.io/07mj6cc3wyee", tc.FileUrl); // link is built from the HOST…
        Assert.Contains("op=upload_form", Assert.Single(getUrls), StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        // …while the bytes go to the file form's node, query intact — never the url or torrent form.
        Assert.Equal("https://s5.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&utype=reg&ptype=ppd", call.Endpoint);
        Assert.DoesNotContain("upload_type=url", call.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("s2.gamezizo.com", call.Endpoint, StringComparison.Ordinal);

        // The exact four fields Uploady's own uploader sends — the family default would add five more.
        Assert.Equal(
            new[] { "file_public", "keepalive", "sess_id", "utype" },
            call.ExtraFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("sess_demo_16ch", call.ExtraFields["sess_id"]); // scraped from the form, not the cookie
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal("1", call.ExtraFields["file_public"]);
        Assert.Equal("1", call.ExtraFields["keepalive"]);
        Assert.Equal("https://uploady.io", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_WebForm_SessionExpired_ReportsItAndClearsTheCookie()
    {
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml), // no upload form → cookie is stale
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Null(credentials.SessionCookie); // cleared so the next attempt re-signs-in
    }

    [Fact]
    public async Task RunAsync_FileOverTheTenGibibyteCap_RejectedBeforeAnyTransfer()
    {
        List<UploadCall> calls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(ValidCookieCredentials()) with { FileSize = 10241L * 1024 * 1024 };
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>()); // never started streaming
        Assert.Empty(calls);                            // and never hit the node
    }

    [Fact]
    public async Task RefreshStorage_ReadsTheMyAccountDashboard_NotMyFiles()
    {
        List<string> getUrls = [];
        UploadyPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(MyAccountHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            ValidCookieCredentials(), handler, ProxyChoice.Direct, CancellationToken.None);

        // Uploady's my_files carries no storage figures at all, so the seam must point at my_account.
        string url = Assert.Single(getUrls);
        Assert.Contains("op=my_account", url, StringComparison.Ordinal);
        Assert.DoesNotContain("op=my_files", url, StringComparison.Ordinal);

        Assert.NotNull(usage);
        Assert.Equal(0L, usage!.Value.UsedBytes);          // "Space used 0.00 MB"
        Assert.Equal(Quota1000Gb, usage.Value.QuotaBytes); // "1000.00 GB total" — NOT the 10 GB bandwidth card
    }

    [Fact]
    public async Task CheckAccount_SignedIn_KeepsTheCookieAsTheCredential_AndReadsStorage()
    {
        FakeAuthService auth = new("xfss_uploady_like");
        UploadyPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(MyAccountHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "typed_name", password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss_uploady_like", result.SessionCookie); // the credential is the cookie…
        Assert.Null(result.ApiKey);                              // …never an API key
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Equal(Quota1000Gb, result.StorageQuotaBytes);

        // Uploady's dashboard exposes no username in a shape the family scrape recognises, so the name
        // the user typed is kept rather than blanked. Cosmetic — sign-in keys on the cookie.
        Assert.Equal("typed_name", result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccount_LandsOnLoginPage_FailsWithDetail()
    {
        FakeAuthService auth = new("xfss_uploady_like");
        UploadyPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Detail); // the full page goes to the Details dialog
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "Uploady",
        Username = "typed_name",
        SessionCookie = "xfss_uploady_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null, // unpinned → valid against any proxy, so no WebView pop.
    };

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "Uploady",
        Credentials = credentials,
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

    private sealed class FakeAuthService(string? cannedCookie) : IInteractiveAuthService
    {
        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
            => Task.FromResult<InteractiveAuthResult?>(
                cannedCookie is null ? null : new InteractiveAuthResult(cannedCookie, CapturedUsername: null));
    }
}
