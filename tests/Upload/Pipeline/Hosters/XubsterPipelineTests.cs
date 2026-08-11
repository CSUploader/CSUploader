// <copyright file="XubsterPipelineTests.cs" company="CSUploader">
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
/// Xubster — classic XFileSharing whose guest upload works despite a sweep concluding otherwise, on
/// nodes that move between hosts AND ports. Fixtures are the real responses, trimmed.
/// </summary>
public class XubsterPipelineTests
{
    /// <summary>The real signed-out limits call: a node, the GUEST cap, and the blocklist.</summary>
    private const string LimitsXml = """
        <Data>
          <ExtAllowed></ExtAllowed>
          <ExtNotAllowed>EXE Files|*.exe|PHP Files|*.php|PHP.JPEG Files|*.php.jpeg|PHP.JPG Files|*.php.jpg|SH Files|*.sh|APK Files|*.apk</ExtNotAllowed>
          <MaxUploadFilesize>10</MaxUploadFilesize>
          <ServerURL>https://x100.xubster.ink:8443/cgi-bin</ServerURL>
          <SessionID></SessionID>
          <SiteName>XUBSTER.COM</SiteName>
        </Data>
        """;

    /// <summary>The signed-in <c>?op=upload</c> page. ⚠ Two forms with the same script — the file one
    /// first, the URL importer second.</summary>
    private const string UploadFormHtml = """
        <form name="file" enctype="multipart/form-data" action="https://x13.xubster.ink:8443/cgi-bin/upload.cgi?upload_type=file" method="post">
          <input type="hidden" name="sess_id" value="spi5vaiju350nolv">
          <input type="hidden" name="utype" value="reg">
        </form>
        <form name="url" action="https://x13.xubster.ink:8443/cgi-bin/upload.cgi?upload_type=url" method="post">
          <input type="hidden" name="sess_id" value="spi5vaiju350nolv">
        </form>
        <script>max_upload_filesize: '500';</script>
        """;

    /// <summary>The real account table — the same shape World Files uses, which is why both patterns
    /// live on the base now.</summary>
    private const string AccountPageHtml = """
        <a href="https://xubster.com/?op=logout">Logout</a>
        <TABLE>
          <TR><TD style="width:35%">Username</TD><TD><b>LynfordAudie</b></TD></TR>
          <TR><TD>Used space</TD><TD><b>0.00 of 1 GB</b></TD></TR>
        </TABLE>
        """;

    private const string UploadOkJson = """[{"file_code":"xc035tgxallp","file_status":"OK"}]""";

    [Fact]
    public void Xubster_IsAnonymousAndAccount_WithACapEitherSide()
    {
        XubsterPipeline pipeline = new();

        Assert.Equal("Xubster", pipeline.Name);

        // It was on the "sweep concluded no anonymous" list. The node says otherwise — measured by
        // uploading real bytes as a guest, the fourth time that sweep's method has been wrong.
        Assert.True(pipeline.SupportsAnonymousUpload);

        Assert.Equal(10L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));
        Assert.Equal(500L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("Xubster"));
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Xubster"));
        Assert.Equal("xubster.com", FileHosterClient.FileHosters["Xubster"]);
    }

    [Fact]
    public void TheUploadPageIsOpUpload_NotTheFamilysUploadForm()
    {
        // ?op=upload_form exists and answers 200 — with the homepage, which carries no form and no
        // sess_id. Pointing the base at it reports a good session as expired.
        (string upload, string account) = new XubsterPipeline().RoutesForTests;

        Assert.Equal("https://xubster.com/?op=upload", upload);
        Assert.Equal("https://xubster.com/?op=my_account", account);
    }

    [Theory]
    [InlineData("https://x100.xubster.ink:8443/cgi-bin", "https://x100.xubster.ink:8443/cgi-bin/upload.cgi?upload_type=file&utype=anon")]
    [InlineData("https://x21.xubster.ink/cgi-bin", "https://x21.xubster.ink/cgi-bin/upload.cgi?upload_type=file&utype=anon")]
    public async Task RunAsync_Anonymous_UsesTheNodeVerbatim_PortAndAll(string serverUrl, string expected)
    {
        // The node moves between hosts (x13/x21/x100), lives on a DIFFERENT domain (.ink), and is not
        // consistently on 443. Anything reconstructed rather than used verbatim breaks on one of those.
        List<UploadCall> calls = [];
        XubsterPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(
                LimitsXml.Replace("https://x100.xubster.ink:8443/cgi-bin", serverUrl, StringComparison.Ordinal)),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://xubster.com/xc035tgxallp", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        UploadCall call = Assert.Single(calls);
        Assert.Equal(expected, call.Endpoint);
        Assert.Equal("anon", call.ExtraFields["utype"]);
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_Anonymous_WithNoServerUrl_FailsWithoutSendingBytes()
    {
        bool uploaded = false;
        XubsterPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult("<Data><MaxUploadFilesize>10</MaxUploadFilesize></Data>"),
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
    public async Task RunAsync_WithAnAccount_PostsToTheFileFormsNode_NotTheUrlImporters()
    {
        // Both forms on the page point at the same script and differ only in upload_type — filedot
        // lost an upload to exactly this by taking the wrong one.
        List<string> gets = [];
        List<UploadCall> calls = [];
        XubsterPipeline pipeline = new(
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
        Assert.Equal("https://xubster.com/xc035tgxallp", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Contains(gets, g => g.EndsWith("?op=upload", StringComparison.Ordinal));

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://x13.xubster.ink:8443/cgi-bin/upload.cgi?upload_type=file", call.Endpoint);
        Assert.DoesNotContain("upload_type=url", call.Endpoint, StringComparison.Ordinal);
        Assert.Equal("spi5vaiju350nolv", call.ExtraFields["sess_id"]);
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("index.php", true)]
    [InlineData("shell.php.jpg", true)]
    [InlineData("shell.php.jpeg", true)]
    [InlineData("install.sh", true)]
    [InlineData("app.apk", true)]
    [InlineData("part1.rar", false)]
    [InlineData("movie.mkv", false)]
    [InlineData("notes.txt", false)]

    // ⚠ The blocked tokens are short and ordinary as SUBSTRINGS — "sh" alone appears in a large share
    // of real release names. Matching anywhere in the name rather than at the extension would refuse
    // files this host accepts, which is a worse failure than the one the list exists to prevent.
    [InlineData("fresh-release.rar", false)]
    [InlineData("exercise.zip", false)]
    [InlineData("apkg-notes.txt", false)]
    [InlineData("php-tutorial.mp4", false)]
    [InlineData("archive.exe.rar", false)]
    public void TheBlocklistIsCheckedLocally(string fileName, bool blocked)
    {
        // XFileSharing enforces ExtNotAllowed at the END of an upload, so without this the user pays
        // for the whole transfer to be told no.
        Assert.Equal(blocked, new XubsterPipeline().RejectedFileExtensionReason(fileName) is not null);
    }

    [Fact]
    public async Task RunAsync_ABlockedExtension_IsRefusedBeforeAnyHttp()
    {
        bool touched = false;
        XubsterPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(UploadFormHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext(anonymous: false) with { FileName = "setup.exe", FilePath = @"C:\nope\setup.exe" };

        Assert.Contains(
            await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None)),
            e => e is AttemptFailed f && f.Reason.Contains(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(touched);
    }

    [Fact]
    public async Task CheckAccount_ReadsTheAccountTable_ThroughTheBase()
    {
        // Neither pattern is this host's own any more: its theme has no fa-user icon and states usage
        // as "0.00 of 1 GB" (no unit on the used figure), and both moved to the base once World Files
        // turned out to ship identical markup. This pins that they still reach here.
        XubsterPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["xfss=sess-from-login; path=/"], "https://xubster.com/?op=my_files")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "LynfordAudie", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("LynfordAudie", result.DerivedUsername);
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Equal(1024L * 1024 * 1024, result.StorageQuotaBytes);
    }

    [Theory]
    [InlineData(true, 10L)]
    [InlineData(false, 500L)]
    public async Task RunAsync_AFileOverTheCapForItsPath_IsRejectedWithoutAnyHttp(bool anonymous, long capMb)
    {
        bool touched = false;
        XubsterPipeline pipeline = new(
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
        HosterName = "Xubster",
        Credentials = anonymous
            ? new FileHosterLoginDto { Id = 0, FileHosterName = "Xubster", IsAnonymous = true }
            : new FileHosterLoginDto
            {
                Id = 7,
                FileHosterName = "Xubster",
                IsAnonymous = false,
                Username = "LynfordAudie",
                SessionCookie = "xfss-value",
                SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
                PinnedProxyId = null,
            },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> ExtraFields);
}
