// <copyright file="FileStorePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
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
/// FileStore — the host whose apex this app cannot reach. Its pages sit behind a Cloudflare
/// interactive challenge and its upload nodes do not, so the sign-in browser captures the node and
/// the session and nothing here ever fetches a page. Every fixture is a real node reply.
/// </summary>
public class FileStorePipelineTests : IDisposable
{
    private const string Node = "https://srv9.filestore.me/cgi-bin/upload.cgi?upload_type=file&utype=reg";
    private const string Session = "3cf6u0lupfkba6f3";

    private const string UploadOkJson = """[{"file_status":"OK","file_code":"i5vrp6ofbl4g"}]""";
    private const string RefusedJson = """[{"file_status":"uploads are not enabled for your account type","file_code":"undef"}]""";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "csu-fs-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public FileStorePipelineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }

    // ── Identity and wiring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileStore_IsAccountOnly_OnTheBrowserSignInPath()
    {
        FileStorePipeline pipeline = new();

        Assert.Equal("FileStore", pipeline.Name);

        // Measured at the node, not read off a page: utype=anon earns "uploads are not enabled for
        // your account type".
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.Equal(250L * 1024 * 1024, pipeline.MaxFileSize);
        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);

        // Session-cookie family: the app's browser is the only thing that can reach this host's pages
        // at all, and there is no key for a user to paste.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("FileStore"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("FileStore"));
        Assert.Equal("filestore.me", FileHosterClient.FileHosters["FileStore"]);
    }

    // ── The node's reply ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseUploadResponse_BuildsTheLinkFromTheFileCode()
    {
        (string? link, string? error) = FileStorePipeline.ParseUploadResponse(new HttpResponseSnapshot(200, UploadOkJson, []));

        Assert.Null(error);
        Assert.Equal("https://filestore.me/i5vrp6ofbl4g", link);
    }

    [Fact]
    public void ParseUploadResponse_TheAccountTypeRefusal_NamesTheLikelierCause()
    {
        // Measured: a LAPSED SESSION produces this exact wording, because an unknown sess_id just
        // looks anonymous to the node. Repeating the host's words alone would send a user with a
        // perfectly good account hunting for an upgrade they don't need.
        (string? link, string? error) = FileStorePipeline.ParseUploadResponse(new HttpResponseSnapshot(200, RefusedJson, []));

        Assert.Null(link);
        Assert.Contains("expired", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sign in again", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(500, "ERROR: Server don't allow uploads at the moment", "500")]
    [InlineData(200, """[{"file_status":"unallowed extension","file_code":"undef"}]""", "unallowed extension")]
    [InlineData(200, """[{"file_status":"OK","file_code":"undef"}]""", "named no file")]
    [InlineData(200, "[]", "no result")]
    [InlineData(200, "<html>nope</html>", "wasn't JSON")]
    public void ParseUploadResponse_ExplainsEveryOtherFailure(int status, string body, string fragment)
    {
        (string? link, string? error) = FileStorePipeline.ParseUploadResponse(new HttpResponseSnapshot(status, body, []));

        Assert.Null(link);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── What the sign-in browser hands back ───────────────────────────────────────────────────────

    [Fact]
    public void ParseProbeResult_ReadsTheNodeAndTheSession()
    {
        (string? node, string? session) = FileStorePipeline.ParseProbeResult(
            $$"""{"node":"{{Node}}","sess":"{{Session}}"}""");

        Assert.Equal(Node, node);
        Assert.Equal(Session, session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"sess":"3cf6u0lupfkba6f3"}""")]
    [InlineData("""{"node":"https://srv9.filestore.me/cgi-bin/upload.cgi"}""")]
    public void ParseProbeResult_AnIncompleteCapture_IsNoSignIn(string? probe)
    {
        (string? node, string? session) = FileStorePipeline.ParseProbeResult(probe);

        Assert.True(node is null || session is null);
    }

    [Fact]
    public void ParseProbeResult_ANodeSomewhereElse_IsRefused()
    {
        // The node is whatever a page said, so it is checked before this app posts a file at it: a
        // template change — or an injected one — must not redirect an upload off-host.
        (string? node, _) = FileStorePipeline.ParseProbeResult(
            """{"node":"https://evil.example.test/cgi-bin/upload.cgi","sess":"3cf6u0lupfkba6f3"}""");

        Assert.Null(node);
    }

    // ── Signing in ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAccount_CapturesBothHalves_ThroughTheBrowser()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult(
                "unused-cookie-value",
                "csuprobe",
                null,
                $$"""{"node":"{{Node}}","sess":"{{Session}}"}"""));

        FileStorePipeline pipeline = new(auth.Object);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);

        // The session comes from the PROBE rather than the captured cookie: they are the same value
        // on this host, and only the probe proves the page was reachable at all.
        Assert.Equal(Session, result.SessionCookie);
        Assert.Equal(Node, result.ApiKey);

        // No signed-in page renders the account name, so its cookie is the only source there is.
        Assert.Equal("csuprobe", result.DerivedUsername);

        auth.Verify(
            a => a.AcquireSessionCookieAsync(
                It.Is<InteractiveAuthSpec>(s =>
                    s.CookieName == "xfss"
                    && s.UsernameCookieName == "login"
                    && s.SuccessProbeScript != null
                    && s.SuccessProbeScript.Contains("op=upload_form", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<ProxyChoice?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAccount_ACancelledSignIn_IsNotAnAccount()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InteractiveAuthResult?)null);

        AccountCheckResult result = await new FileStorePipeline(auth.Object).CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("cancelled", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_WithNoBrowser_SaysWhyItNeedsOne()
    {
        AccountCheckResult result = await new FileStorePipeline().CheckAccountAsync(
            "csuprobe", string.Empty, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Cloudflare", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Re-checking, which this host cannot support ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshAccount_KeepsAStoredSignIn_AndSaysItCouldNotBeChecked()
    {
        // Every page that could confirm a session is behind the challenge. Reporting "invalid" would
        // auto-disable working accounts over a check that is impossible rather than failed — so the
        // account is kept and the message says exactly that.
        AccountCheckResult result = await new FileStorePipeline().RefreshAccountAsync(
            Node, Session, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(Session, result.SessionCookie);
        Assert.Equal(Node, result.ApiKey);
        Assert.Contains("can't be re-checked", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, Session)]
    [InlineData(Node, "")]
    public async Task RefreshAccount_WithoutBothHalves_IsNotAnAccount(string? node, string session)
    {
        AccountCheckResult result = await new FileStorePipeline().RefreshAccountAsync(
            node, session, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    // ── Uploading ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_PostsTheFamilyFieldSetToTheCapturedNode()
    {
        List<UploadCall> calls = [];
        FileStorePipeline pipeline = new(null, (filePath, endpoint, fields) =>
        {
            calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://filestore.me/i5vrp6ofbl4g", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        UploadCall call = Assert.Single(calls);
        Assert.Equal(Node, call.Endpoint);

        // sess_id IS the session cookie on this host — the reason an upload needs no page fetch.
        Assert.Equal(Session, call.Fields["sess_id"]);
        Assert.Equal("reg", call.Fields["utype"]);
        Assert.Equal("1", call.Fields["keepalive"]);
    }

    [Fact]
    public async Task Run_Anonymously_IsRefusedLocally()
    {
        bool uploaded = false;
        FileStorePipeline pipeline = new(null, (_, _, _) =>
        {
            uploaded = true;
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        });

        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "FileStore", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no anonymous upload", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task Run_WithoutACapturedNode_SaysToSignInAgain()
    {
        // The node and the session come from the same sign-in and neither can be recovered without
        // it: this app cannot reach the page that issues them.
        bool uploaded = false;
        FileStorePipeline pipeline = new(null, (_, _, _) =>
        {
            uploaded = true;
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        });

        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 1, FileHosterName = "FileStore", IsAnonymous = false, SessionCookie = Session, ApiKey = null,
            },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no saved sign-in", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task Run_AFileOverTheCap_IsRefusedWithoutAnyHttp()
    {
        bool uploaded = false;
        FileStorePipeline pipeline = new(null, (_, _, _) =>
        {
            uploaded = true;
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        });

        AttemptContext ctx = MakeContext() with { FileSize = (250L * 1024 * 1024) + 1 };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("250 MiB", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(uploaded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

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

    private AttemptContext MakeContext()
    {
        string path = Path.Combine(_tempDir, "release.r00");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, new byte[4096]);
        }

        return new AttemptContext
        {
            AttemptId = Guid.NewGuid(),
            FilePath = path,
            FileName = "release.r00",
            FileSize = 4096,
            HosterName = "FileStore",
            Credentials = new FileHosterLoginDto
            {
                Id = 1,
                FileHosterName = "FileStore",
                IsAnonymous = false,
                SessionCookie = Session,
                ApiKey = Node,
            },
            Proxy = ProxyChoice.Direct,
            Handler = MakeHandler(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
