// <copyright file="EliteFilePipelineTests.cs" company="CSUploader">
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
/// EliteFile — stock XFileSharing on the web-form path, so most of it is the base's already-tested
/// behaviour. What's pinned here is the two things that would go wrong silently: the link domain (the
/// host stores files on a DIFFERENT domain than it serves) and the inherited 1 GiB cap (this host has
/// none). Fixtures are trimmed from a signed-in capture (2026-08-03).
/// </summary>
public class EliteFilePipelineTests
{
    // ?op=upload_form — the file form's action already names upload_type=file&utype=reg, so the
    // family scrape needs no rewriting here (unlike filedot.to and ShareMods).
    private const string UploadFormHtml = """
        <!doctype html><html><body>
        <a href="/?op=logout">Logout</a>
        <form id="uploadfile" action="https://s1.elitefile.net/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="sess_demo_16ch">
          <input type="hidden" name="utype" value="reg">
        </form>
        <script>ext_not_allowed: '', max_upload_files: '15', max_upload_filesize: '0',</script>
        </body></html>
        """;

    // ?op=my_account — both storage figures, and the bandwidth widget that must not be mistaken for them.
    private const string AccountHtml = """
        <!doctype html><html><body>
        <a href="/?op=logout">Logout</a>
        <div class="form-group" style="background:#3080e8;"><center>
          <i class="fad fa-user-tie fa-3x"></i> <br />
          <label style="color:#fff;"><b>demo_account</b> </label>
          <p style="color:#fff;">Free Account </p>
        </center></div>
        <!-- the decoy that produced "Signed in as Settings": same icon family, nav item -->
        <a class="nav-link"><i class="fad fa-user-tie"></i> Settings</a>
        <div class="widget p-3 storage position-relative">
          <span>Used Space</span> <div class="price"><sup>GB</sup>1.50 / of 488 <sup>GB</sup></div>
          <a href="/?op=payments">Extend storage</a>
        </div>
        <div class="widget p-3 traffic position-relative">
          <span>Traffic available</span> <div class="price"><sup>GB</sup>30</div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_BuildsTheLinkOnTheDomainTheServerNames_NotTheSiteYouUploadedTo()
    {
        // The upload answers {"domain":"https://elfile.net",…} and the host's own result page links
        // elfile.net. Building from Host would hand the user elitefile.net/<code> — a different domain
        // than the one the file actually lives on.
        List<(string Endpoint, IReadOnlyDictionary<string, string> Fields)> calls = [];
        EliteFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (_, endpoint, extra, _, _) =>
            {
                calls.Add((endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"domain":"https://elfile.net","file_code":"s12kr30vhg0m","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://elfile.net/s12kr30vhg0m", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string endpoint, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://s1.elitefile.net/cgi-bin/upload.cgi?upload_type=file&utype=reg", endpoint);

        // Six fields, as captured — the family default sends nine.
        Assert.Equal(
            new[] { "keepalive", "link_pass", "link_rcpt", "sess_id", "to_folder", "utype" },
            fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("sess_demo_16ch", fields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_WithNoDomainInTheResponse_FallsBackToTheSite()
    {
        // Every other host in the family omits the field; they must keep working.
        EliteFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """[{"file_code":"s12kr30vhg0m","file_status":"OK"}]""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://elitefile.net/s12kr30vhg0m", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_LargeFile_IsNotRejected_BecauseThisHostHasNoCap()
    {
        // The base defaults to 1 GiB. Inheriting it would skip every larger file at queue time without
        // ever asking the host — the bug Uploadrar shipped with.
        Assert.Null(new EliteFilePipeline().MaxFileSize);

        bool uploaded = false;
        EliteFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"domain":"https://elfile.net","file_code":"big","file_status":"OK"}]""", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = 4L * 1024 * 1024 * 1024 }; // 4 GiB
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.True(uploaded);
    }

    [Fact]
    public async Task CheckAccount_ReadsBothStorageFigures_NotTheTrafficWidget()
    {
        FakeAuthService auth = new("xfss_elite_like");
        EliteFilePipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);

        // The reported bug: the family's generic fa-user scrape returned the nav item's label, so every
        // account showed as "Signed in as Settings".
        Assert.Equal("demo_account", result.DerivedUsername);
        Assert.NotEqual("Settings", result.DerivedUsername);

        Assert.Equal(1536L * 1024 * 1024, result.StorageUsedBytes);           // "1.50 GB"
        Assert.Equal(488L * 1024 * 1024 * 1024, result.StorageQuotaBytes);    // "of 488 GB"

        // The identical widget alongside is a 30 GB/day bandwidth allowance.
        Assert.NotEqual(30L * 1024 * 1024 * 1024, result.StorageQuotaBytes);
    }

    [Theory]
    // The header block carries the name; the nav item carries a label. Both use fa-user-tie, and only
    // the header has fa-3x — which is the whole distinction.
    [InlineData("""<i class="fad fa-user-tie fa-3x"></i><br /><label><b>demo_account</b></label>""", "demo_account")]
    [InlineData("""<a class="nav-link"><i class="fad fa-user-tie"></i> Settings</a>""", null)]
    [InlineData("<div>no header here</div>", null)]
    public void ParseUsername_TakesTheHeaderName_NotTheNavLabel(string html, string? expected)
        => Assert.Equal(expected, EliteFilePipeline.ParseUsername(html));

    [Fact]
    public void EliteFile_IsAccountOnly_OnTheSessionCookieCredential()
    {
        EliteFilePipeline pipeline = new();
        Assert.Equal("EliteFile", pipeline.Name);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.Equal("elitefile.net", FileHosterClient.FileHosters["EliteFile"]);

        // No REST API exists (/api/upload/server 404s), so there is no key to paste — sign-in only.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("EliteFile"));
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\release.rar",
        FileName = "release.rar",
        FileSize = 100,
        HosterName = "EliteFile",
        Credentials = new FileHosterLoginDto
        {
            Id = 1,
            FileHosterName = "EliteFile",
            Username = "typed_name",
            SessionCookie = "xfss_elite_like",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
            PinnedProxyId = null,
        },
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

    private sealed class FakeAuthService(string? cannedCookie) : IInteractiveAuthService
    {
        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
            => Task.FromResult<InteractiveAuthResult?>(
                cannedCookie is null ? null : new InteractiveAuthResult(cannedCookie, CapturedUsername: null));
    }
}
