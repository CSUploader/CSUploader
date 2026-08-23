// <copyright file="TeraBytezPipelineTests.cs" company="CSUploader">
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
/// TeraBytez on the web-form path. Fixtures are trimmed from a signed-in browser capture
/// (2026-08-02) with session values replaced. The protocol itself is stock, so what's pinned is this
/// theme's own markup — a storage widget that prints its unit BEFORE the number, a user menu reading
/// "Profile" that must not be mistaken for the account name, and the shorter multipart set.
/// </summary>
public class TeraBytezPipelineTests
{
    // /upload/ — the file form carries its own action and comes first, so the family scrape resolves
    // the node unaided (unlike filedot.to, whose file form has none).
    private const string UploadPageHtml = """
        <!doctype html><html><body>
        <a href="https://terabytez.org/logout/">Logout</a>
        <form id="uploadfile" class="upload-form bg-white position-relative" action="https://fs26.terabytez.org/cgi-bin/upload.cgi?utype=reg">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
          <input type="text" name="link_rcpt" class="myForm" size=24 maxlength=42>
          <input type="text" name="link_pass" class="myForm" size=8>
        </form>
        <form method="post" action="" onsubmit="if(!this.tos.checked){ return(false); }"></form>
        <script>ic_default: '1', ext_allowed: '', ext_not_allowed: '', max_upload_files: '1', max_upload_filesize: '100',</script>
        </body></html>
        """;

    // /account/ — the storage widget, the "My username" field, and the /logout/ link. The Traffic
    // widget is byte-identical in structure and is BANDWIDTH; the user menu says "Profile".
    private const string AccountPageHtml = """
        <!doctype html><html><body>
        <a href="https://terabytez.org/logout/">Logout</a>
        <a class="nav-link"><i class="fad fa-user"></i> Profile</a>
        <div class="widget p-3 storage position-relative">
          <span>Used Space</span> <div class="price"><sup>GB</sup>1.50</div>
          <a href="https://terabytez.org/premium/">Extend storage</a>
        </div>
        <div class="widget p-3 traffic position-relative">
          <span>Traffic available</span> <div class="price"><sup>MB</sup>5000</div>
          <a href="https://terabytez.org/premium/">Extend traffic</a>
        </div>
        <div class="form-group"><label>My username</label>
          <input type="text" readonly class="form-control-plaintext" value="demo_account"></div>
        </body></html>
        """;

    // What a stale cookie gets served: the logged-out login page — no upload form, no logout link.
    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Login</title></head><body>
        <form method="post" action="/"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_ScrapesTheUploadPage_AndPostsTheCapturesFieldSet()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        TeraBytezPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadPageHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_status":"OK","file_code":"n944679mr7i2"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://terabytez.org/n944679mr7i2", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // Its own page, not the family's ?op=upload_form.
        Assert.Equal("https://terabytez.org/upload/", Assert.Single(getUrls));

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://fs26.terabytez.org/cgi-bin/upload.cgi?utype=reg", call.Endpoint);

        // Seven fields: the family default minus the "upload" button and keepalive.
        Assert.Equal(
            new[] { "file_descr", "file_public", "link_pass", "link_rcpt", "sess_id", "to_folder", "utype" },
            call.ExtraFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("sess_demo_16ch", call.ExtraFields["sess_id"]); // scraped from the form, not the cookie
        Assert.Equal("1", call.ExtraFields["file_public"]);
        Assert.Equal("https://terabytez.org", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_SessionExpired_ReportsItAndClearsTheCookie()
    {
        TeraBytezPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Null(credentials.SessionCookie); // cleared → next attempt re-signs-in
    }

    [Fact]
    public async Task RunAsync_CloudflareFiveTwenty_FailsTheAttempt_ButKeepsTheSignIn()
    {
        // The nastier half of the 520 Data Vaults surfaced. On this path "no upload form on the page"
        // is what tells us the cookie went stale — so an edge error, which also has no upload form,
        // used to be read as a dead session: cookie discarded, WebView re-popped, and the user asked
        // to sign in again over a blip that lasted seconds.
        FileHosterLoginDto credentials = ValidCookieCredentials();
        TeraBytezPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("error code: 520"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<AuthFailed>());
        Assert.Equal("xfss_tbz_like", credentials.SessionCookie); // the sign-in survives
    }

    [Fact]
    public async Task RunAsync_FileOverTheHundredMegabyteCap_RejectedBeforeAnyTransfer()
    {
        // The smallest cap of any hoster in the tree, so this is the one most likely to bite: a
        // release-sized file must be refused at queue time, not after minutes of upload.
        List<UploadCall> calls = [];
        TeraBytezPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadPageHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(ValidCookieCredentials()) with { FileSize = (100L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CheckAccount_ReadsUsedSpace_AndTheRealName_NotProfile()
    {
        FakeAuthService auth = new("xfss_tbz_like");
        TeraBytezPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss_tbz_like", result.SessionCookie);
        Assert.Null(result.ApiKey);

        // The family's fa-user scrape would return "Profile" here — the exact wrong name Uploady once
        // displayed. This theme keeps the real one in a readonly field.
        Assert.Equal("demo_account", result.DerivedUsername);

        // "<sup>GB</sup>1.50" — unit BEFORE the number, and read binary.
        Assert.Equal(1536L * 1024 * 1024, result.StorageUsedBytes);

        // The quota is the registered tier's 10 GB, which this page never renders — the homepage
        // advertises "Unlimited Storage" and only PREMIUM actually is. Reporting null here would show
        // Available as "Unlimited" for a 10 GB account.
        Assert.Equal(10L * 1024 * 1024 * 1024, result.StorageQuotaBytes);

        // The 5000 MB two boxes along is a traffic allowance in identical markup — if the scrape had
        // drifted onto it, Used would read 5000 MB.
        Assert.NotEqual(5000L * 1024 * 1024, result.StorageUsedBytes);
    }

    [Theory]
    [InlineData(AccountType.Free, 100L * 1024 * 1024)]
    [InlineData(AccountType.Premium, 5000L * 1024 * 1024)]
    public void MaxFileSizeFor_FollowsTheTier(AccountType type, long expected)
    {
        TeraBytezPipeline pipeline = new();
        FileHosterLoginDto credentials = ValidCookieCredentials();
        credentials.AccountType = type;

        Assert.Equal(expected, pipeline.MaxFileSizeFor(credentials));

        // The no-credentials default must be the conservative one: premium is undetectable on this
        // host, so every account we persist looks Free.
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSize);
    }

    [Fact]
    public async Task RefreshStorage_ReadsTheAccountPage_WithTheStoredCookie()
    {
        List<string> getUrls = [];
        TeraBytezPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            ValidCookieCredentials(), handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.Equal("https://terabytez.org/account/", Assert.Single(getUrls));
        Assert.NotNull(usage);
        Assert.Equal(1536L * 1024 * 1024, usage!.Value.UsedBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, usage.Value.QuotaBytes);
    }

    [Fact]
    public void TeraBytez_IsAccountOnly_OnTheSessionCookieCredential()
    {
        TeraBytezPipeline pipeline = new();
        Assert.Equal("TeraBytez", pipeline.Name);
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSize);

        // Probed 2026-08-02: an anonymous classic post answers 500 "Uploads not enabled for this type
        // of users", and the chunked route takes the bytes only to refuse at import_file.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.Equal("terabytez.org", FileHosterClient.FileHosters["TeraBytez"]);
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("TeraBytez"));
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "TeraBytez",
        Username = "typed_name",
        SessionCookie = "xfss_tbz_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null, // unpinned → valid against any proxy, so no WebView pop.
    };

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 100,
        HosterName = "TeraBytez",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
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
