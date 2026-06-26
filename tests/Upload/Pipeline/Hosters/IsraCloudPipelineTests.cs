// <copyright file="IsraCloudPipelineTests.cs" company="CSUploader">
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
/// Tests for the web-form (no-API) path of <see cref="XFileSharingApiPipeline"/> as exercised by
/// <see cref="IsraCloudPipeline"/>: upload-server discovery from <c>?op=upload_form</c>, the
/// isra-specific classic field set, my_account verification/storage scraping, and the
/// non-interactive storage refresh. Fixtures mirror the live isra.cloud capture (2026-06-26).
/// </summary>
public class IsraCloudPipelineTests
{
    // The logged-in ?op=upload_form page: a <form id="uploadfile" action="…upload.cgi…"> with the
    // hidden sess_id — exactly the shape captured from the live site.
    private const string UploadFormHtml = """
        <!doctype html><html><body>
        <form id="uploadfile" action="https://fs25.isra.cloud/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="sess_demo">
          <input type="hidden" name="utype" value="reg">
          <input type="file" multiple="multiple" id="file_0">
        </form>
        </body></html>
        """;

    // Logged-in my_account: a logout link, the username after the fa-user icon, and the "Used space"
    // panel (label + figure in sibling divs, the figure div's class carrying a stray digit).
    private const string MyAccountHtml = """
        <!doctype html><html><body>
        <ul class="menu"><li><a href="https://isra.cloud/?op=logout" title="Logout">Logout</a></li></ul>
        <a class="dropdown" aria-haspopup="true"><i class="fa fa-user"></i>pkjmq41030<i class="fa fa-angle-down"></i></a>
        <div class="box"><i class="fa fa-server"></i><div class="txtarea">
          <div class="txt1">Used space</div>
          <div class="txt2">1.50 TB</div>
        </div></div>
        </body></html>
        """;

    // What my_account looks like when the cookie no longer authenticates us: the login page (no
    // logout link, no "Used space" panel).
    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Sign in</title></head><body>
        <form action="/login.html" method="post"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    private const long OneAndHalfTiB = (long)(1.5 * (1L << 40)); // 1.50 TB scraped, binary units.

    [Fact]
    public void NameAndMaxFileSize_AreIsracloudWithNoClientCap()
    {
        IsraCloudPipeline pipeline = new();
        Assert.Equal("Isracloud", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize); // "no client cap" — server enforces its own limit.
    }

    [Fact]
    public async Task RunAsync_WebForm_DiscoversServerFromUploadForm_AndPostsIsraFieldSet()
    {
        Queue<string> getResponses = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"s077tizxaza4","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];

        IsraCloudPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        // A valid stored session cookie → no WebView; the upload server comes from upload_form.
        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://isra.cloud/s077tizxaza4", tc.FileUrl);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://fs25.isra.cloud/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        // The exact captured field set: sess_id from the form, empty file_public, NO upload button.
        Assert.Equal("sess_demo", call.ExtraFields["sess_id"]);
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal(string.Empty, call.ExtraFields["file_public"]);
        Assert.Equal("1", call.ExtraFields["keepalive"]);
        Assert.False(call.ExtraFields.ContainsKey("upload"));
        // Browser-shaped same-site classic headers carry the apex Origin.
        Assert.Equal("https://isra.cloud", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_WebForm_UploadFormMissing_ReportsSessionExpired_AndClearsCookie()
    {
        // The cookie no longer authenticates us → ?op=upload_form serves a page with no upload form.
        Queue<string> getResponses = new(new[] { LoginPageHtml });

        IsraCloudPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AuthFailed>());
        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        // The stale cookie is cleared so the next attempt re-signs-in (repo is null here → in-memory only).
        Assert.Null(credentials.SessionCookie);
    }

    [Fact]
    public async Task CheckAccount_WebForm_SignedIn_ScrapesUsernameAndUsedSpace_NoApiKey()
    {
        FakeAuthService auth = new("xfss_isra_like");
        IsraCloudPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(MyAccountHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("pkjmq41030", result.DerivedUsername);    // scraped from the fa-user menu item
        Assert.Equal(OneAndHalfTiB, result.StorageUsedBytes);  // "1.50 TB"
        Assert.Null(result.StorageQuotaBytes);                 // no advertised cap → Unlimited
        Assert.Equal("xfss_isra_like", result.SessionCookie);  // the credential is the cookie…
        Assert.Null(result.ApiKey);                            // …not an API key
    }

    [Fact]
    public async Task CheckAccount_WebForm_LandsOnLoginPage_FailsWithDetail()
    {
        FakeAuthService auth = new("xfss_isra_like");
        IsraCloudPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Detail); // full page goes to the Details dialog
    }

    [Fact]
    public async Task RefreshStorage_WithStoredCookie_ScrapesUsedSpace_QuotaNull()
    {
        IsraCloudPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(MyAccountHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        StorageUsage? usage = await pipeline.RefreshStorageAsync(ValidCookieCredentials(), handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(OneAndHalfTiB, usage!.Value.UsedBytes);
        Assert.Null(usage.Value.QuotaBytes);
    }

    [Fact]
    public async Task RefreshStorage_NoStoredCookie_ReturnsNull_WithoutWebView()
    {
        IsraCloudPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(MyAccountHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        FileHosterLoginDto noCookie = new() { Id = 5, FileHosterName = "Isracloud", Username = "u" };
        StorageUsage? usage = await pipeline.RefreshStorageAsync(noCookie, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.Null(usage); // no stored session → can't refresh; caller keeps its snapshot.
    }

    [Theory]
    [InlineData("Used space</td><td>0.00 TB</td>", 0L)]
    [InlineData("""<div class="txt1">Used space</div><div class="txt2">1.50 TB</div>""", OneAndHalfTiB)]
    [InlineData("Used space: 512 MB", 512L << 20)]
    [InlineData("Used space 2 GB", 2L << 30)]
    [InlineData("Used space 1,25 GB", (long)(1.25 * (1L << 30)))]  // comma decimal separator
    [InlineData("no figure here", null)]
    public void TryParseUsedSpaceBytes_ParsesUnitsAndDecimals(string html, long? expected)
        => Assert.Equal(expected, XFileSharingApiPipeline.TryParseUsedSpaceBytes(html));

    [Fact]
    public async Task RunAsync_WebForm_UploadFormWithoutSessIdInput_FallsBackToCookieValue()
    {
        // A form that omits the hidden sess_id — the base falls back to the session-cookie value.
        const string FormNoSessId = """
            <form id="uploadfile" action="https://fs9.isra.cloud/cgi-bin/upload.cgi?upload_type=file&utype=reg">
              <input type="file" id="file_0">
            </form>
            """;
        Queue<string> getResponses = new(new[] { FormNoSessId });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"z","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];

        IsraCloudPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        await foreach (UploadEvent _ in pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None)) { }

        // No hidden sess_id on the form → the stored cookie value ("xfss_isra_like") is used.
        Assert.Equal("xfss_isra_like", Assert.Single(calls).ExtraFields["sess_id"]);
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "Isracloud",
        Username = "pkjmq41030",
        SessionCookie = "xfss_isra_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null, // unpinned → valid against any proxy, so no WebView pop.
    };

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "Isracloud",
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
        {
            InteractiveAuthResult? result = cannedCookie is null
                ? null
                : new InteractiveAuthResult(cannedCookie, CapturedUsername: null);
            return Task.FromResult(result);
        }
    }
}
