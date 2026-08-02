// <copyright file="FiledotPipelineTests.cs" company="CSUploader">
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
/// filedot.to on the web-form path. Fixtures are trimmed from a signed-in browser capture
/// (2026-08-02) with session values replaced. What's pinned here is where this fork DIFFERS from its
/// siblings: the node comes from <c>GET /server</c> rather than the page's form action, the multipart
/// set is shorter than the family default, and both the storage quota and the extension blocklist are
/// real figures that a plausible-looking mis-read would get wrong.
/// </summary>
public class FiledotPipelineTests
{
    private const string NodeJson = """{"url":"https://fs28.cobytes.cc/cgi-bin"}""";

    // /upload/ — the file form carries NO action (its script fetches /server), and the ONLY action on
    // the page belongs to the remote-URL uploader. Reproduced in live document order because that is
    // exactly the trap: the family's scrape would find a real-looking upload.cgi and post a file to
    // the URL-import endpoint.
    private const string UploadPageHtml = """
        <!doctype html><html><body>
        <a href="https://filedot.to/logout/" class="btn_blue">Logout</a>
        <div>Max file size is 5120 Mb</div>
        <form id="uploadfile">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
          <input type="text" name="link_pass" class="myForm" size=8>
          <Select name="to_folder"><option value="">..</option></Select>
        </form>
        <form method="post" id="uploadurl" action="https://fs31.cobytes.cc/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
        </form>
        <script>ext_allowed: '', ext_not_allowed: 'exe|jpg|jpeg|gif|png', max_upload_files: '50',</script>
        </body></html>
        """;

    // /account — storage, the account name, and the logout link this fork uses instead of ?op=logout.
    // The "Traffic available today" row is a DAILY BANDWIDTH allowance sitting directly under the
    // storage row, and it happens to equal the per-file size cap: three ways to read it wrongly.
    private const string AccountPageHtml = """
        <!doctype html><html><body>
        <a href="https://filedot.to/logout/">Logout</a>
        <Table width=100% class="inf">
        <TR><TD colspan=2><h3>Account Details</h3></TD></TR>
        <TR><TD>Username</TD><TD><b>demo_account</b></TD></TR>
        <TR><TD>Account balance</TD><TD><b>$0</b></TD></TR>
        <TR><TD>Used space</TD><TD><b>1.50 of 10240 GB</b></TD></TR>
        <TR><TD>Traffic available today</TD><TD><b>5120 Mb</b></TD></TR>
        </Table>
        </body></html>
        """;

    // What a stale cookie gets served: the logged-out login page. No logout link anywhere on it.
    private const string LoginPageHtml = """
        <!doctype html><html><head><title>Login</title></head><body>
        <form method="post" action="/"><input name="login"><input name="password" type="password"></form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_TakesTheNodeFromServerLookup_NotThePagesUrlUploaderAction()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) =>
            {
                getUrls.Add(url);
                return Task.FromResult(url.EndsWith("/server", StringComparison.Ordinal) ? NodeJson : UploadPageHtml);
            },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"z7jed7kf1ju7","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://filedot.to/z7jed7kf1ju7", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The uploader page, then the keyless node lookup.
        Assert.Equal(["https://filedot.to/upload/", "https://filedot.to/server"], getUrls);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://fs28.cobytes.cc/cgi-bin/upload.cgi", call.Endpoint);

        // The whole reason this host needed a hook: the page's own action would have sent the file to
        // fs31's URL-IMPORT endpoint, which takes a link and would never have stored these bytes.
        Assert.DoesNotContain("upload_type=url", call.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("fs31", call.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PostsTheCapturesFieldSet_NotTheFamilyDefault()
    {
        List<UploadCall> calls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(url.EndsWith("/server", StringComparison.Ordinal) ? NodeJson : UploadPageHtml),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"z7jed7kf1ju7","file_status":"OK"}]""", Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(ValidCookieCredentials()), CancellationToken.None));

        UploadCall call = Assert.Single(calls);

        // Six fields, exactly as captured — no link_rcpt, no "upload" button, no keepalive. The family
        // default sends nine, and this parser is field-presence sensitive.
        Assert.Equal(
            new[] { "file_descr", "file_public", "link_pass", "sess_id", "to_folder", "utype" },
            call.ExtraFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("sess_demo_16ch", call.ExtraFields["sess_id"]); // scraped from the page, not the cookie
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal("0", call.ExtraFields["file_public"]);          // the family default is 1
        Assert.Equal("https://filedot.to", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_SessionExpired_ClearsTheCookieWithoutAskingForANode()
    {
        // /server answers a signed-out caller perfectly happily, so the session check has to come off
        // the PAGE. Without it we'd upload with a dead sess_id, which XFileSharing treats as
        // anonymous — and this host refuses anonymous, so the user would see a baffling
        // "not enabled for your account type" instead of "sign in again".
        List<string> getUrls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(LoginPageHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = ValidCookieCredentials();
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Null(credentials.SessionCookie);                  // cleared → next attempt re-signs-in
        Assert.DoesNotContain("/server", Assert.Single(getUrls), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("holiday.jpg", true)]
    [InlineData("photo.PNG", true)]      // the check is case-insensitive
    [InlineData("setup.exe", true)]
    [InlineData("release.rar", false)]
    [InlineData("release.r00", false)]
    [InlineData("clip.mp4", false)]      // video is fine here — the opposite of Uploadrar
    public void IsBlockedExtension_MatchesTheHostsOwnList(string fileName, bool blocked)
        => Assert.Equal(blocked, FiledotPipeline.IsBlockedExtension(fileName));

    [Fact]
    public async Task RunAsync_BlockedExtension_RefusedBeforeAnyTransfer()
    {
        // The host enforces its blocklist at the upload itself, so the whole file would otherwise be
        // spent to earn the refusal.
        List<UploadCall> calls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(url.EndsWith("/server", StringComparison.Ordinal) ? NodeJson : UploadPageHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(ValidCookieCredentials()) with { FilePath = @"C:\nope\cover.jpg", FileName = "cover.jpg" };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("JPG", reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RunAsync_FileOverTheFiveGigabyteCap_RejectedBeforeAnyTransfer()
    {
        List<UploadCall> calls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(url.EndsWith("/server", StringComparison.Ordinal) ? NodeJson : UploadPageHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(ValidCookieCredentials()) with { FileSize = (5120L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CheckAccount_ReadsBothUsedAndQuota_AndTheAccountName()
    {
        FakeAuthService auth = new("xfss_filedot_like");
        FiledotPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("xfss_filedot_like", result.SessionCookie);
        Assert.Null(result.ApiKey);
        Assert.Equal("demo_account", result.DerivedUsername);

        // "1.50 of 10240 GB" — unlike most of this family the quota is published, so Available shows a
        // real number. Both read binary.
        Assert.Equal(1536L * 1024 * 1024, result.StorageUsedBytes);
        Assert.Equal(10240L * 1024 * 1024 * 1024, result.StorageQuotaBytes);

        // …and the 5120 Mb on the next row is BANDWIDTH. If either figure had picked it up, one of the
        // two asserts above would be 5 GB.
        Assert.NotEqual(5120L * 1024 * 1024, result.StorageQuotaBytes);
    }

    [Fact]
    public async Task RefreshStorage_ReadsTheAccountPage_WithTheStoredCookie()
    {
        List<string> getUrls = [];
        FiledotPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            ValidCookieCredentials(), handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.Equal("https://filedot.to/account", Assert.Single(getUrls));
        Assert.NotNull(usage);
        Assert.Equal(1536L * 1024 * 1024, usage!.Value.UsedBytes);
        Assert.Equal(10240L * 1024 * 1024 * 1024, usage.Value.QuotaBytes);
    }

    [Theory]
    [InlineData(NodeJson, "https://fs28.cobytes.cc/cgi-bin", null)]
    [InlineData("""{"url":"https://fs28.cobytes.cc/cgi-bin/"}""", "https://fs28.cobytes.cc/cgi-bin", null)]
    [InlineData("""{"msg":"nope"}""", null, "no upload node")]
    [InlineData("<html>maintenance</html>", null, "no upload node")]
    public void ParseNode_ReadsTheUrl_OrExplainsItself(string json, string? expected, string? errorFragment)
    {
        (string? node, string? error) = FiledotPipeline.ParseNode(json);
        Assert.Equal(expected, node);
        if (errorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(errorFragment, error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Filedot_IsAccountOnly_OnTheSessionCookieCredential()
    {
        FiledotPipeline pipeline = new();
        Assert.Equal("Filedot", pipeline.Name);
        Assert.Equal(5120L * 1024 * 1024, pipeline.MaxFileSize);

        // Probed 2026-08-02: an anonymous post (empty sess_id, utype=anon) to its own node answers
        // "uploads are not enabled for your account type". Enabling this would offer an upload that
        // can only fail.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.Equal("filedot.to", FileHosterClient.FileHosters["Filedot"]);
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("Filedot"));
    }

    private static FileHosterLoginDto ValidCookieCredentials() => new()
    {
        Id = 1,
        FileHosterName = "Filedot",
        Username = "typed_name",
        SessionCookie = "xfss_filedot_like",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        PinnedProxyId = null, // unpinned → valid against any proxy, so no WebView pop.
    };

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\release.rar",
        FileName = "release.rar",
        FileSize = 100,
        HosterName = "Filedot",
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
