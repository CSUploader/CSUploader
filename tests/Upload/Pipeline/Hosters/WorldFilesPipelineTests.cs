// <copyright file="WorldFilesPipelineTests.cs" company="CSUploader">
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

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// World Files — classic XFileSharing whose guest upload is live while the site renders no guest form,
/// so the node has to come from the keyless limits call. Fixtures are the real responses, trimmed.
/// </summary>
public class WorldFilesPipelineTests
{
    /// <summary>The real signed-out limits call: a node and the GUEST cap, handed to a caller with no
    /// session at all.</summary>
    private const string LimitsXml = """
        <Data>
          <ExtAllowed></ExtAllowed>
          <ExtNotAllowed></ExtNotAllowed>
          <MaxUploadFilesize>5000</MaxUploadFilesize>
          <ServerURL>https://wfs04.world-files.com/cgi-bin</ServerURL>
          <SessionID></SessionID>
          <SiteName>World Files</SiteName>
        </Data>
        """;

    /// <summary>The signed-in upload page.</summary>
    private const string UploadFormHtml = """
        <form id="uploadfile" action="https://wfs02.world-files.com/cgi-bin/upload.cgi?upload_type=file&utype=reg" method="post">
          <input type="hidden" name="sess_id" value="f3ag11ag7g33n7f5">
          <input type="hidden" name="utype" value="reg">
        </form>
        <script>var max_upload_filesize: '10000';</script>
        """;

    /// <summary>The real account table. ⚠ The used figure carries no unit of its own, and there is no
    /// <c>fa-user</c> icon anywhere for the family's name scrape to anchor on.</summary>
    private const string AccountPageHtml = """
        <a href="https://world-files.com/?op=logout">Logout</a>
        <TABLE>
          <TR><TD style="width:35%">Username</TD><TD><b>LynfordAudie</b></TD></TR>
          <TR><TD>Used space</TD><TD><b>0.00 of 500 GB</b></TD></TR>
        </TABLE>
        """;

    private const string UploadOkJson = """[{"file_status":"OK","file_code":"xyowm59tnxe1"}]""";

    [Fact]
    public void WorldFiles_IsAnonymousAndAccount_WithACapEitherSide()
    {
        WorldFilesPipeline pipeline = new();

        Assert.Equal("World Files", pipeline.Name);

        // Measured at the node, not read off a page: the site renders NO guest form (signed out,
        // ?op=upload_form 302s to the login) and the node takes the bytes anyway.
        Assert.True(pipeline.SupportsAnonymousUpload);

        Assert.Equal(5000L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));
        Assert.Equal(10000L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("World Files"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("World Files"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("World Files"));
        Assert.Equal("world-files.com", FileHosterClient.FileHosters["World Files"]);
    }

    [Fact]
    public void TheAccountPageIsMyAccount_WhereBothFiguresLive()
    {
        // The family default (?op=my_files) loads and shows the signed-in chrome, but carries neither
        // the name nor the storage row — so pointing at it would silently lose both.
        Assert.Equal("https://world-files.com/?op=my_account", new WorldFilesPipeline().AccountPageUrlForTests);
    }

    [Fact]
    public async Task RunAsync_Anonymous_TakesTheNodeFromTheKeylessLimitsCall()
    {
        List<string> gets = [];
        List<UploadCall> calls = [];
        WorldFilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(LimitsXml); },
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://world-files.com/xyowm59tnxe1", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        Assert.Contains(gets, g => g.Contains("op=api_get_limits", StringComparison.Ordinal));

        // <ServerURL> names the cgi-bin DIRECTORY; the script and the family's guest query are appended.
        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://wfs04.world-files.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        Assert.Equal("anon", call.ExtraFields["utype"]);
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_Anonymous_WithNoServerUrl_FailsWithoutSendingBytes()
    {
        // The node rotates and is asked for per upload, so a limits call that answers something else
        // (a WAF page, an outage) must stop here rather than post 5 GB at a guess.
        bool uploaded = false;
        WorldFilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<Data><MaxUploadFilesize>5000</MaxUploadFilesize></Data>"),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        Assert.False(uploaded);
        Assert.Contains(events.OfType<AttemptFailed>(), f => f.Reason.Contains("ServerURL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_ScrapesTheFormAndPostsToItsNode()
    {
        List<string> gets = [];
        List<UploadCall> calls = [];
        WorldFilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(UploadFormHtml); },
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: false), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://world-files.com/xyowm59tnxe1", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Contains(gets, g => g.Contains("op=upload_form", StringComparison.Ordinal));

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://wfs02.world-files.com/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        Assert.Equal("f3ag11ag7g33n7f5", call.ExtraFields["sess_id"]);
    }

    [Fact]
    public void ParseAccountUsername_ReadsTheTableRow_BecauseThisThemeHasNoFaUserIcon()
    {
        // The family default anchors on a fa-user icon this theme doesn't have, so it saved whatever
        // was typed. What gets stored is what the next sign-in POSTs, and this host signs in with the
        // USERNAME — so its own spelling of it beats the box.
        Assert.Equal("LynfordAudie", new WorldFilesPipeline().ParseAccountUsernameForTests(AccountPageHtml));
        Assert.Null(new WorldFilesPipeline().ParseAccountUsernameForTests("<html><body>signed out</body></html>"));
    }

    [Theory]
    [InlineData("""<TR><TD>Used space</TD><TD><b>0.00 of 500 GB</b></TD></TR>""", 0L, 500L * 1024 * 1024 * 1024)]
    [InlineData("""<TR><TD>Used space</TD><TD><b>1.50 of 500 GB</b></TD></TR>""", 1610612736L, 500L * 1024 * 1024 * 1024)]
    [InlineData("""<TR><TD>Used space</TD><TD><b>512.00 MB of 500 GB</b></TD></TR>""", 512L * 1024 * 1024, 500L * 1024 * 1024 * 1024)]
    public void ParseStorageUsage_ReadsThisForksRow_IncludingTheUnitlessUsedFigure(string html, long used, long quota)
    {
        // "0.00 of 500 GB" — the used figure is stated in the QUOTA's unit, which is why neither of
        // the base's two bar patterns matches it. The third case pins that an explicit unit still wins
        // if this fork ever prints one.
        (long? u, long? q) = new WorldFilesPipeline().ParseStorageUsageForTests(html);

        Assert.Equal(used, u);
        Assert.Equal(quota, q);
    }

    [Fact]
    public void ParseStorageUsage_WithNoSuchRow_FallsBackToTheFamilyBar()
    {
        (long? used, long? quota) = new WorldFilesPipeline().ParseStorageUsageForTests("<html><body>nothing here</body></html>");

        Assert.Null(used);
        Assert.Null(quota);
    }

    [Fact]
    public async Task CheckAccount_ReadsMyAccount_AndReportsBothFigures()
    {
        List<string> gets = [];
        WorldFilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["xfss=sess-from-login; path=/"], "https://world-files.com/?op=my_files")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "LynfordAudie", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("sess-from-login", result.SessionCookie);
        Assert.Equal("LynfordAudie", result.DerivedUsername);
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Equal(500L * 1024 * 1024 * 1024, result.StorageQuotaBytes);
        Assert.Contains(gets, g => g.Contains("op=my_account", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 5000L)]
    [InlineData(false, 10000L)]
    public async Task RunAsync_AFileOverTheCapForItsPath_IsRejectedWithoutAnyHttp(bool anonymous, long capMb)
    {
        bool touched = false;
        WorldFilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(anonymous ? LimitsXml : UploadFormHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(anonymous) with { FileSize = (capMb * 1024 * 1024) + 1 };

        Assert.Single(await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None)), e => e is AttemptFailed);
        Assert.False(touched);
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeContext(bool anonymous) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.rar",
        FileName = "x.rar",
        FileSize = 4096,
        HosterName = "World Files",
        Credentials = anonymous
            ? new FileHosterLoginDto { Id = 0, FileHosterName = "World Files", IsAnonymous = true }
            : new FileHosterLoginDto
            {
                Id = 6,
                FileHosterName = "World Files",
                IsAnonymous = false,
                Username = "LynfordAudie",
                SessionCookie = "xfss-value",
                SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
                PinnedProxyId = null,
            },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> ExtraFields);
}
