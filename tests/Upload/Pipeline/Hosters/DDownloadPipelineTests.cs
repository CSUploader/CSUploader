// <copyright file="DDownloadPipelineTests.cs" company="CSUploader">
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
/// DDownload on the web-form path. Every fixture detail here is one this fork moved: the uploader onto
/// <c>/upload</c>, the logout link to a plain <c>/logout</c>, and storage/identity into a redesigned
/// dashboard. Shapes are from a browser capture of a signed-in upload plus the live account page
/// (2026-08-01), session and account values replaced.
/// </summary>
public class DDownloadPipelineTests
{
    // The logged-in /upload page.
    private const string UploadPageHtml = """
        <!doctype html><html><body>
        <a class="nav-link" href="/logout">Logout</a>
        <form id="uploadfile" action="https://eu-hydra5.zeuscdn.org/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="api_sess_demo_77ch">
          <input type="hidden" name="utype" value="reg">
          <input type="file" name="file_0">
        </form>
        </body></html>
        """;

    // The redesigned dashboard (?op=my_account). Note it links plain /logout — the family probe looks
    // for ?op=logout and would reject this perfectly good session.
    private const string DashboardHtml = """
        <!doctype html><html><body>
        <a class="ma-logout-link" href="/logout"><i class="fas fa-sign-out-alt"></i> Logout</a>
        <div class="ma-stats-grid">
          <a href="/my-files" class="ma-stat-card">
            <div class="ma-stat-label"><i class="fas fa-hdd"></i> Storage Used</div>
            <div class="ma-stat-value">68 KB</div>
            <div class="ma-stat-sub">of 5.00 TB</div>
          </a>
          <div class="ma-stat-card">
            <div class="ma-stat-label"><i class="fas fa-id-badge"></i> Account Status</div>
            <div class="ma-stat-value">Free</div>
          </div>
        </div>
        <div class="ma-form-row">
          <div class="ma-form-label">Username</div>
          <div class="ma-form-field"><input type="text" readonly value="demo.account@example.invalid"></div>
        </div>
        </body></html>
        """;

    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Login - DDownload</title></head><body>
        <form method="POST" action="https://ddownload.com/" name="FL"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_WebForm_ScrapesTheUploadPage_AndPostsTheBrowsersFieldSet()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        DDownloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadPageHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"xc2w350fwnke","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None));

        Assert.Equal("https://ddownload.com/xc2w350fwnke", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The uploader is at /upload here — this fork has no ?op=upload_form.
        string url = Assert.Single(getUrls);
        Assert.EndsWith("/upload", url, StringComparison.Ordinal);
        Assert.DoesNotContain("op=upload_form", url, StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://eu-hydra5.zeuscdn.org/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);

        // The browser's own eight fields — an EMPTY file_public, and neither file_descr nor the
        // "upload" button the family default sends.
        Assert.Equal(
            new[] { "file_public", "keepalive", "link_pass", "link_rcpt", "sess_id", "to_folder", "utype" },
            call.ExtraFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("api_sess_demo_77ch", call.ExtraFields["sess_id"]); // scraped from the form
        Assert.Equal(string.Empty, call.ExtraFields["file_public"]);
        Assert.False(call.ExtraFields.ContainsKey("upload"));
        Assert.False(call.ExtraFields.ContainsKey("file_descr"));
        Assert.Equal("https://ddownload.com", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task CheckAccount_AcceptsAPlainLogoutLink_AndReadsTheDashboard()
    {
        // The family probe looks for "?op=logout"; this dashboard links "/logout". Without the
        // override that would reject a good sign-in with "the account page didn't load as logged-in".
        FakeAuthService auth = new("xfss_dd_like");
        DDownloadPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(DashboardHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        // Empty typed username — session-cookie mode hides that field, so this is what really arrives.
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss_dd_like", result.SessionCookie);
        Assert.Null(result.ApiKey);
        Assert.Equal("demo.account@example.invalid", result.DerivedUsername);
        Assert.Equal(68L * 1024, result.StorageUsedBytes);                 // "68 KB"
        Assert.Equal(5L * 1024 * 1024 * 1024 * 1024, result.StorageQuotaBytes); // "of 5.00 TB"
    }

    [Fact]
    public async Task CheckAccount_LandsOnLoginPage_Fails()
    {
        FakeAuthService auth = new("xfss_dd_like");
        DDownloadPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task RunAsync_WebForm_SessionExpired_ClearsTheCookie()
    {
        DDownloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml), // no upload form → cookie is stale
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Null(credentials.SessionCookie);
    }

    [Fact]
    public void DDownload_SignsIn_RatherThanRequiringAnAffiliateApiKey()
    {
        DDownloadPipeline pipeline = new();
        Assert.Equal("DDownload", pipeline.Name);

        // No client-side cap: the host publishes no per-file figure anywhere.
        Assert.Null(pipeline.MaxFileSize);
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("DDownload"));
        Assert.Equal("ddownload.com", FileHosterClient.FileHosters["DDownload"]);

        // Session cookie, NOT ApiKey: the REST API works but its key comes only from the Affiliate
        // Dashboard, so demanding one would gate the first upload behind enabling affiliate.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("DDownload"));
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "DDownload",
        Username = "demo.account@example.invalid",
        SessionCookie = "xfss_dd_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 4096,
        HosterName = "DDownload",
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
