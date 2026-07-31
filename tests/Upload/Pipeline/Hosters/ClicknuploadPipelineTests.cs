// <copyright file="ClicknuploadPipelineTests.cs" company="CSUploader">
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
/// Clicknupload on the web-form path — the third user of <see cref="XFileSharingApiPipeline"/>'s
/// no-API mode after isra.cloud and Uploady, and the first whose multipart matches the family default
/// exactly. Fixtures are trimmed from a signed-in browser capture (2026-07-31), session values
/// replaced. What's pinned here is where this fork MOVED things: the uploader onto
/// <c>?op=my_account.html</c>, and storage into a line of header text.
/// </summary>
public class ClicknuploadPipelineTests
{
    // ?op=my_account.html: the logout link, the storage line, and BOTH upload forms in live document
    // order — the file uploader first, then the remote-URL one, which posts to the same upload.cgi
    // path and must not be chosen.
    private const string AccountPageHtml = """
        <!doctype html><html><body>
        <div class="UserHead" style="display:none;"><span>&#9776;</span> Balance: <strong>$0</strong>,
          Used space: <strong>1.50 GB</strong>, Traffic available today: <strong>Unlimited</strong> </div>
        <a href="https://clicknupload.click/?op=logout">Logout</a>
        <form id="uploadfile" action="https://white01.clicknupload.net/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
          <input type="hidden" name="link_rcpt" value="">
          <input type="hidden" name="link_pass" value="">
          <input type="file" name="file_0">
        </form>
        <form method="post" id="uploadurl" action="https://white01.clicknupload.net/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
        </form>
        </body></html>
        """;

    // What any logged-in page serves once the cookie stops authenticating: the login page.
    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Login</title></head><body>
        <form method="post" action="/"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_WebForm_ScrapesTheAccountPage_AndPostsTheFamilyDefaultFieldSet()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        ClicknuploadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"bs5yo561020t","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None));

        Assert.Equal("https://clicknupload.click/bs5yo561020t", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The uploader is on the ACCOUNT page — this fork has no ?op=upload_form at all.
        string url = Assert.Single(getUrls);
        Assert.Contains("op=my_account.html", url, StringComparison.Ordinal);
        Assert.DoesNotContain("op=upload_form", url, StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://white01.clicknupload.net/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        Assert.DoesNotContain("upload_type=url", call.Endpoint, StringComparison.Ordinal);

        // Exactly the family's nine fields — the whole reason this hoster needed no override.
        Assert.Equal(
            new[] { "file_descr", "file_public", "keepalive", "link_pass", "link_rcpt", "sess_id", "to_folder", "upload", "utype" },
            call.ExtraFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("sess_demo_16ch", call.ExtraFields["sess_id"]); // scraped from the form, not the cookie
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal("1", call.ExtraFields["file_public"]);
        Assert.Equal("Start upload", call.ExtraFields["upload"]);
        Assert.Equal("https://clicknupload.click", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_WebForm_SessionExpired_ReportsItAndClearsTheCookie()
    {
        ClicknuploadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml), // no upload form → cookie is stale
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Null(credentials.SessionCookie); // cleared so the next attempt re-signs-in
    }

    [Fact]
    public async Task RunAsync_FileOverTheTwoGibibyteCap_RejectedBeforeAnyTransfer()
    {
        List<UploadCall> calls = [];
        ClicknuploadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(ValidCookieCredentials()) with { FileSize = (2048L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CheckAccount_SignedIn_ReadsUsedSpace_ButNoQuota()
    {
        FakeAuthService auth = new("xfss_cnu_like");
        ClicknuploadPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "typed_name", password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss_cnu_like", result.SessionCookie); // the credential is the cookie…
        Assert.Null(result.ApiKey);                          // …never an API key
        Assert.Equal(1536L * 1024 * 1024, result.StorageUsedBytes); // "1.50 GB", binary

        // "Traffic available today: Unlimited" sits in the same div and is BANDWIDTH — it must never
        // be read as a storage quota. No quota published → Available renders "Unlimited".
        Assert.Null(result.StorageQuotaBytes);
    }

    [Fact]
    public async Task RefreshStorage_ReadsTheAccountPage_WithTheStoredCookie()
    {
        List<string> getUrls = [];
        ClicknuploadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            ValidCookieCredentials(), handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.Contains("op=my_account.html", Assert.Single(getUrls), StringComparison.Ordinal);
        Assert.NotNull(usage);
        Assert.Equal(1536L * 1024 * 1024, usage!.Value.UsedBytes);
        Assert.Null(usage.Value.QuotaBytes);
    }

    [Fact]
    public void Clicknupload_IsAccountOnly_WithTheSessionCookieCredential()
    {
        ClicknuploadPipeline pipeline = new();
        Assert.Equal("Clicknupload", pipeline.Name);
        Assert.Equal(2048L * 1024 * 1024, pipeline.MaxFileSize);

        // Guests are refused outright by the host ("uploads are not enabled for your account type"),
        // so anonymous must stay off — flipping this on would offer an upload that always fails.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Clicknupload"));
        Assert.Equal("clicknupload.click", FileHosterClient.FileHosters["Clicknupload"]);
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("Clicknupload"));
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "Clicknupload",
        Username = "typed_name",
        SessionCookie = "xfss_cnu_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null, // unpinned → valid against any proxy, so no WebView pop.
    };

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "Clicknupload",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
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
