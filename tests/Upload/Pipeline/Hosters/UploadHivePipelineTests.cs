// <copyright file="UploadHivePipelineTests.cs" company="CSUploader">
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
/// UploadHive — anonymous and account, both on <see cref="XFileSharingApiPipeline"/>. Fixtures are the
/// real bodies from captures of an anonymous and a registered upload (2026-08-08), each verified by
/// uploading. Nearly everything here is a deviation from the family default, and each one would send
/// files or credentials somewhere useless if it were dropped.
/// </summary>
public class UploadHivePipelineTests
{
    private const string ServerJson = """{"url":"https://fs430.uploadhive.com/cgi-bin"}""";
    private const string UploadedJson = """[{"file_code":"888rv70d6hum","file_status":"OK"}]""";

    /// <summary>The signed-in /account/ page, copied from the capture's markup. None of the family's
    /// markers appear on it — that is the point of the three overrides it exercises.</summary>
    private const string AccountHtml = """
        <div class="UserHead"><span>&#9776;</span> Welcome back <b>csuprobe</b>, this is your userpanel </div>
        <div class="AcctBox mrgn bg2"><div class="AcctBoxInner">
          <div class="txt1">Used space</div> <div class="txt2">0.00 of 98 GB</div>
        </div></div>
        <a href="https://uploadhive.com/logout/" class="btn_blue">Logout</a>
        """;

    [Fact]
    public async Task RunAsync_Anonymous_AsksTheHostForItsNode_AndSendsItsOwnFieldSet()
    {
        // The node comes from GET /server, which is what the site's own uploader calls. The earlier
        // approach scraped the page — where the anonymous FILE form has no action at all, so the only
        // upload.cgi action belongs to the REMOTE-URL form and files would have gone to URL import.
        List<string> gets = [];
        string? endpoint = null;
        Dictionary<string, string>? fields = null;

        UploadHivePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(ServerJson); },
            uploadOverride: (_, url, extra, _, _) =>
            {
                endpoint = url;
                fields = new Dictionary<string, string>(extra);
                return Task.FromResult(new HttpResponseSnapshot(200, UploadedJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(AnonymousContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Contains("/server", Assert.Single(gets), StringComparison.Ordinal);
        Assert.Equal("https://fs430.uploadhive.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", endpoint);
        Assert.DoesNotContain("upload_type=url", endpoint!, StringComparison.Ordinal);

        // The field set its own uploader sends — file_descr rather than the family's file_0_descr,
        // file_public=1, and none of mode/keepalive/submit_btn.
        Assert.Equal(string.Empty, fields!["sess_id"]);
        Assert.Equal("anon", fields["utype"]);
        Assert.Equal("1", fields["file_public"]);
        Assert.True(fields.ContainsKey("file_descr"));
        Assert.False(fields.ContainsKey("mode"));
        Assert.False(fields.ContainsKey("keepalive"));

        Assert.Equal("https://uploadhive.com/888rv70d6hum", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_SignedIn_SendsTheSessionCookieAsTheSessionId()
    {
        // Both captures are byte-identical apart from two fields, and the sess_id IS the xfss cookie —
        // compared value-for-value in the capture. So unlike the rest of this family there is no
        // ?op=upload_form page to scrape for one.
        string? endpoint = null;
        Dictionary<string, string>? fields = null;

        UploadHivePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(ServerJson),
            uploadOverride: (_, url, extra, _, _) =>
            {
                endpoint = url;
                fields = new Dictionary<string, string>(extra);
                return Task.FromResult(new HttpResponseSnapshot(200, UploadedJson, Array.Empty<string>()));
            });

        AttemptContext ctx = AnonymousContext() with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 5,
                FileHosterName = "UploadHive",
                IsAnonymous = false,
                Username = "someone",
                SessionCookie = "xfss-16-chars-ab",
                SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
                PinnedProxyId = null,
            },
        };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal("https://fs430.uploadhive.com/cgi-bin/upload.cgi?upload_type=file&utype=reg", endpoint);
        Assert.Equal("xfss-16-chars-ab", fields!["sess_id"]);
        Assert.Equal("reg", fields["utype"]);
    }

    [Fact]
    public void AccountPage_YieldsSignedIn_TheName_AndTheStorage()
    {
        // Its /account/ page has NO ?op=logout, NO fa-user icon and NO class="storage", so all three
        // family scrapes return nothing here — the sign-in reads as failed and the account saves blank.
        UploadHivePipeline pipeline = new();

        Assert.True(pipeline.LooksSignedInForTests(AccountHtml));
        Assert.Equal("csuprobe", pipeline.ParseAccountUsernameForTests(AccountHtml));

        (long? used, long? quota) = pipeline.ParseStorageUsageForTests(AccountHtml);
        Assert.Equal(0L, used);
        Assert.Equal(98L * 1024 * 1024 * 1024, quota);
    }

    [Fact]
    public void AccountPage_WithTheFamilyMarkupInstead_YieldsNothing()
    {
        // Guards the direction: this host must read ITS page, not inherit a match from the family's.
        UploadHivePipeline pipeline = new();
        const string FamilyHtml = """<a href="?op=logout">out</a><i class="fa fa-user"></i>someone<span class="storage"><b>1 MB</b> of <b>2 GB</b></span>""";

        Assert.Null(pipeline.ParseAccountUsernameForTests(FamilyHtml));
        Assert.Equal((null, null), pipeline.ParseStorageUsageForTests(FamilyHtml));
    }

    [Theory]
    [InlineData("rls.part1.rar", null)]
    [InlineData("rls.r00", null)]
    [InlineData("rls.sfv", null)]
    [InlineData("rls.nfo", null)]
    // Declared by the host (ext_not_allowed: '7z|001') and confirmed by uploading one of each: both
    // come back {"file_code":"undef","file_status":"unallowed extension"} AFTER the whole transfer.
    [InlineData("rls.7z", ".7z")]
    [InlineData("rls.001", ".001")]
    public void RejectedFileExtensionReason_MatchesTheHostsOwnBlocklist(string fileName, string? expected)
    {
        string? reason = new UploadHivePipeline().RejectedFileExtensionReason(fileName);

        if (expected is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(expected, reason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UploadHive_IsAnonymousAndAccount_WithNoDeclaredCap()
    {
        UploadHivePipeline pipeline = new();
        Assert.Equal("UploadHive", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // max_upload_filesize is '0', meaning unlimited here — uploads succeed. Inheriting the base's
        // 1 GiB default would silently skip every larger file at queue time.
        Assert.Null(pipeline.MaxFileSize);

        // Its login is a plain form with no captcha, so credentials go in the app's own dialog.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("UploadHive"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("UploadHive"));

        Assert.Equal("uploadhive.com", FileHosterClient.FileHosters["UploadHive"]);
    }

    private static AttemptContext AnonymousContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\probe.rar",
        FileName = "probe.rar",
        FileSize = 4096,
        HosterName = "UploadHive",
        Credentials = new FileHosterLoginDto { FileHosterName = "UploadHive", IsAnonymous = true },
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
}
