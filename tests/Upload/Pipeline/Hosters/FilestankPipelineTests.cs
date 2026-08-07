// <copyright file="FilestankPipelineTests.cs" company="CSUploader">
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
/// Filestank on the YetiShare/blueimp web path. Every fixture below is the shape observed in the
/// signed-in browser capture of 2026-08-01 — the generated <c>uploader.js</c>, the storage node's
/// JSON array, and the account stats endpoint. What's pinned hardest is that the node is
/// authenticated by the scraped <c>_sessionid</c> FIELD rather than by a cookie, and that the ticket
/// is re-scraped per upload because every part of it rotates.
/// </summary>
public class FilestankPipelineTests
{
    private const string Session = "a408l2ekdmaemu6m290q2pqv7t";
    private const string NodeUrl = "https://str2.filestank.com/ajax/file_upload_handler?r=www.filestank.com&p=https&csaKey1=aaa&csaKey2=bbb";

    /// <summary>The three values in their real setting: the node URL is a jQuery-File-Upload option,
    /// the other two are assembled into formData on submit. The decoy <c>url_upload_handler</c> line
    /// is real too — it sits in the same file with a DIFFERENT csaKey pair.</summary>
    private const string UploaderJs = """
        function initUploader(uploadSourceParam) {
          var maxChunkSize = 0;
          if (browserXHR2Support() == true) { maxChunkSize = 100000000; var uploaderMaxSize = 21474836480; }
          $('#fileUpload #uploader').fileupload({
            sequentialUploads: false, limitConcurrentUploads: 8,
            url: 'https://str2.filestank.com/ajax/file_upload_handler?r=www.filestank.com&p=https&csaKey1=aaa&csaKey2=bbb',
            maxFileSize: uploaderMaxSize, maxChunkSize: maxChunkSize, maxNumberOfFiles: 50 });
          $('#rowUrl').prop('src', 'https://str2.filestank.com/ajax/url_upload_handler?csaKey1=zzz&csaKey2=yyy&rowId=' + i);
          $('#fileUpload #uploader').bind('fileuploadsubmit', function (e, data) {
            data.formData = {_sessionid: 'a408l2ekdmaemu6m290q2pqv7t', cTracker: 'a19c479804646fa6e289fcc06b26009a', maxChunkSize: maxChunkSize, folderId: fileFolder, uploadSource: uploadSource};
          });
        }
        """;

    private const string UploadOkJson = """[{"name":"x.avi","size":5225142,"type":"video/avi","error":null,"url":"https://www.filestank.com/0e788016ff766b8e","delete_url":"https://www.filestank.com/0e788016ff766b8e~d?abc","short_url":"0e788016ff766b8e","file_id":"153881"}]""";

    [Fact]
    public async Task RunAsync_ScrapesTheTicketThenUploads_AndReturnsTheNodesUrl()
    {
        List<string> gets = [];
        List<UploadCall> uploads = [];

        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, headers) =>
            {
                gets.Add(url);
                Assert.Equal($"filehosting={Session}", headers!["Cookie"]);
                return Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>()));
            },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), headers));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://www.filestank.com/0e788016ff766b8e", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.StartsWith("https://www.filestank.com/assets/js/uploader.js?r=", Assert.Single(gets), StringComparison.Ordinal);

        UploadCall call = Assert.Single(uploads);
        Assert.Equal(NodeUrl, call.Endpoint);
        Assert.Equal(Session, call.ExtraFields["_sessionid"]);
        Assert.Equal("a19c479804646fa6e289fcc06b26009a", call.ExtraFields["cTracker"]);
        Assert.Equal("100000000", call.ExtraFields["maxChunkSize"]);
        Assert.Equal("-1", call.ExtraFields["folderId"]);
        Assert.Equal("file_manager", call.ExtraFields["uploadSource"]);

        // The node is authenticated by the _sessionid FIELD. The browser sends it no cookie at all;
        // sending one would be a different (unobserved) request shape.
        Assert.NotNull(call.Headers);
        Assert.False(call.Headers!.ContainsKey("Cookie"));
        Assert.Equal("https://www.filestank.com", call.Headers["Origin"]);
    }

    [Fact]
    public async Task RunAsync_ReScrapesTheTicketForEveryUpload()
    {
        // Every part of the ticket rotates — in the capture, two assets rendered seconds apart carried
        // DIFFERENT csaKey pairs. Caching one across a batch would post to a stale node.
        int gets = 0;
        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) =>
            {
                gets++;
                return Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(3, gets);
    }

    [Fact]
    public async Task RunAsync_PerFileError_IsReportedEvenThoughTheEnvelopeLooksFine()
    {
        // The node refuses inside an HTTP 200 with a populated array — reading past it to a missing
        // url would report something far less useful than the node's own sentence.
        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """[{"name":"x.avi","size":0,"error":"File is too large","url":null}]""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("File is too large", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    /// <summary>Verbatim from a live refusal, 2026-08-01 — the node's answer to the 100 MB first
    /// chunk of a release part once the account's daily allowance was gone.</summary>
    private const string DailyCapJson = """[{"size":0,"type":"","name":"Max uploads reached.","error":"You have reached the maximum permitted uploads for today."}]""";

    [Fact]
    public async Task RunAsync_DailyCapReached_FailsThisFileAndThenTheRestOfTheBatchForFree()
    {
        // Filestank allows a limited number of uploads per day. The refusal arrives only AFTER a
        // chunk has been pushed, and every remaining file in the batch would pay the same toll — 80
        // files means gigabytes spent learning the same fact 80 times.
        int scrapes = 0, uploads = 0;
        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) =>
            {
                scrapes++;
                return Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(200, DailyCapJson, Array.Empty<string>()));
            });

        FileHosterLoginDto shared = new()
        {
            Id = 3,
            FileHosterName = "Filestank",
            SessionCookie = Session,
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(4),
        };

        List<UploadEvent> first = await DrainAsync(pipeline.RunAsync(MakeContext() with { Credentials = shared }, CancellationToken.None));
        Assert.Contains("daily upload allowance", Assert.Single(first.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);

        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext() with { Credentials = shared }, CancellationToken.None));
        Assert.Contains("daily upload allowance", Assert.Single(second.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, scrapes);  // the second file didn't even ask for a ticket…
        Assert.Equal(1, uploads);  // …and sent nothing
        Assert.Empty(second.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_DailyCapIsTrackedPerAccount_NotGlobally()
    {
        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, DailyCapJson, Array.Empty<string>())));

        FileHosterLoginDto spent = new() { Id = 4, FileHosterName = "Filestank", SessionCookie = Session, SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(4) };
        await DrainAsync(pipeline.RunAsync(MakeContext() with { Credentials = spent }, CancellationToken.None));

        // A second Filestank account has its own allowance and must still be tried.
        FileHosterLoginDto other = new() { Id = 5, FileHosterName = "Filestank", SessionCookie = Session, SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(4) };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext() with { Credentials = other }, CancellationToken.None));

        Assert.Single(events.OfType<TransferStarted>());
    }

    [Theory]
    [InlineData(DailyCapJson, true)]
    [InlineData("""[{"name":"x","error":"You have reached the maximum permitted uploads for today."}]""", true)]
    [InlineData("""{"files":[{"name":"Max uploads reached.","error":""}]}""", true)]
    // "maximum" alone is not enough — the size refusal says it too, and that one is about the FILE.
    [InlineData("""[{"name":"x.avi","error":"File exceeds the maximum allowed size."}]""", false)]
    [InlineData("""[{"name":"x.avi","error":null,"url":"https://www.filestank.com/abc"}]""", false)]
    [InlineData("not json", false)]
    public void IsDailyCapRefusal_MatchesTheAllowanceWordingOnly(string body, bool expected)
        => Assert.Equal(expected, FilestankPipeline.IsDailyCapRefusal(body));

    [Fact]
    public async Task RunAsync_WithoutASignedInSessionAndNoWebView_FailsBeforeAnyRequest()
    {
        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not scrape"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Filestank" }, // no stored cookie
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("sign in", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_ExpiredStoredCookie_SignsInAgainAndSucceeds()
    {
        // A lapsed session still returns 200 — with the signed-out uploader.js, which has no ticket in
        // it. That is the only signal there is, so it must drive a re-sign-in rather than a hard fail.
        int scrapes = 0, signIns = 0;
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .Callback(() => signIns++)
            .ReturnsAsync(new InteractiveAuthResult(Session, null));

        FilestankPipeline pipeline = new(
            auth.Object,
            loginRepository: null,
            getOverride: (_, _) =>
            {
                scrapes++;
                return Task.FromResult(scrapes == 1
                    ? new HttpResponseSnapshot(200, "function initUploader() { /* signed out */ }", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(1, signIns);  // the stored cookie was good enough to try; only the stale answer forced a WebView
        Assert.Equal(2, scrapes);
        Assert.Single(events.OfType<AuthSucceeded>());
    }

    [Fact]
    public async Task RunAsync_ParallelUploadsOnOneAccount_OpenASingleSignInWindow()
    {
        // Without the per-account gate, ten files starting at once each pop their own WebView.
        int signIns = 0;
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref signIns);
                await Task.Delay(30);
                return (InteractiveAuthResult?)new InteractiveAuthResult(Session, null);
            });

        FilestankPipeline pipeline = new(
            auth.Object,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, UploaderJs, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        // One shared DTO, exactly as a real batch on one account has.
        FileHosterLoginDto shared = new() { Id = 7, FileHosterName = "Filestank" };
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => DrainAsync(pipeline.RunAsync(MakeContext() with { Credentials = shared }, CancellationToken.None))));

        Assert.Equal(1, signIns);
    }

    [Fact]
    public void ParseUploaderScript_TakesTheFileHandlerUrl_NotTheUrlUploadHandler()
    {
        (FilestankPipeline.UploadTicket? ticket, string? error, bool stale) = FilestankPipeline.ParseUploaderScript("Filestank", UploaderJs, 200);

        Assert.Null(error);
        Assert.False(stale);
        Assert.Equal(NodeUrl, ticket!.Value.UploadUrl);   // …csaKey1=aaa, not the url_upload_handler's zzz
        Assert.Equal(Session, ticket.Value.SessionId);
        Assert.Equal("a19c479804646fa6e289fcc06b26009a", ticket.Value.Tracker);
        Assert.Equal(21474836480L, ticket.Value.SessionMaxFileSize);
    }

    [Theory]
    // The script declares the cap twice — a zero placeholder, then the real figure — so the largest
    // wins. All three figures are live: probed signed-out, and captured as trial and as registered.
    [InlineData("var uploaderMaxSize = 0; if (x) { var uploaderMaxSize = 21474836480; }", 21474836480L)] // registered: 20 GiB
    [InlineData("var uploaderMaxSize = 0; if (x) { var uploaderMaxSize = 1073741824; }", 1073741824L)]   // anonymous trial: 1 GiB
    [InlineData("var uploaderMaxSize = 0; if (x) { var uploaderMaxSize = 0; }", 0L)]                     // signed out: may not upload
    [InlineData("no cap here", null)]
    public void ReadSessionMaxSize_TakesTheLargestDeclaration(string js, long? expected)
        => Assert.Equal(expected, FilestankPipeline.ReadSessionMaxSize(js));

    [Theory]
    [InlineData(1073741824L, 500L, null)]                                  // fits
    [InlineData(null, 999_999_999_999L, null)]                             // undeclared → let the server decide
    [InlineData(0L, 10L, "isn't accepting uploads")]                       // signed out is its own sentence…
    [InlineData(1073741824L, 2_000_000_000L, "limit for this account")]    // …and not the same as "too big"
    public void SessionLimitRefusal_SeparatesTooBigFromNotAllowed(long? sessionMax, long fileSize, string? fragment)
    {
        string? refusal = FilestankPipeline.SessionLimitRefusal("Filestank", sessionMax, fileSize, "x.avi");

        if (fragment is null)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.Contains(fragment, refusal!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunAsync_SessionThatMayNotUpload_FailsWithoutSendingAByte()
    {
        // A signed-out visitor is served a complete, valid-looking ticket — node URL, _sessionid and
        // cTracker all present — with a cap of 0. Trusting the ticket alone would push the whole file
        // at a node that was never going to take it.
        string signedOutJs = UploaderJs.Replace("var uploaderMaxSize = 21474836480", "var uploaderMaxSize = 0", StringComparison.Ordinal);

        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, signedOutJs, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("isn't accepting uploads", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_FileOverTheSessionsCap_FailsBeforeTheTransferStarts()
    {
        // The session's own cap beats the static MaxFileSize: an anonymous trial account is capped at
        // 1 GiB where a registered one gets 20 GiB, and only the ticket knows which this is.
        string trialJs = UploaderJs.Replace("var uploaderMaxSize = 21474836480", "var uploaderMaxSize = 1073741824", StringComparison.Ordinal);

        FilestankPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, trialJs, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with { FileSize = 3L * 1024 * 1024 * 1024 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("3 GiB", reason, StringComparison.Ordinal);  // the file
        Assert.Contains("1 GiB", reason, StringComparison.Ordinal);  // the cap
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    [InlineData("function initUploader() { }", 200, true)]   // signed out: 200, no ticket
    [InlineData("", 403, true)]
    [InlineData("", 500, false)]                             // a server fault is not "your session lapsed"
    public void ParseUploaderScript_WithoutATicket_ReportsWhetherTheSessionLooksStale(string js, int status, bool expectStale)
    {
        (FilestankPipeline.UploadTicket? ticket, string? error, bool stale) = FilestankPipeline.ParseUploaderScript("Filestank", js, status);

        Assert.Null(ticket);
        Assert.NotNull(error);
        Assert.Equal(expectStale, stale);
    }

    [Theory]
    [InlineData(UploadOkJson, "https://www.filestank.com/0e788016ff766b8e", null)]
    [InlineData("""{"files":[{"url":"https://www.filestank.com/abc","error":null}]}""", "https://www.filestank.com/abc", null)]
    [InlineData("""{"data":[{"url":"https://www.filestank.com/def"}]}""", "https://www.filestank.com/def", null)]
    [InlineData("""[{"name":"x","error":"Upload failed"}]""", null, "Upload failed")]
    [InlineData("[]", null, "no link")]
    [InlineData("<html>gateway timeout</html>", null, "unreadable")]
    public void ParseUploadResponse_ReadsEitherEnvelope_AndPrefersThePerFileError(string body, string? url, string? errorFragment)
    {
        (string? gotUrl, string? gotError) = FilestankPipeline.ParseUploadResponse("Filestank", new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Equal(url, gotUrl);
        if (errorFragment is null)
        {
            Assert.Null(gotError);
        }
        else
        {
            Assert.Contains(errorFragment, gotError!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ParseAccountStats_ReadsTheStringEncodedByteCounts()
    {
        const string Json = """{"totalActiveFileSize":0,"totalFileStorage":"107374182400","totalFileStorageFormatted":"100.0 GB"}""";

        (long? used, long? quota) = FilestankPipeline.ParseAccountStats(Json);

        Assert.Equal(0L, used);
        Assert.Equal(100L * 1024 * 1024 * 1024, quota); // the "100.0 GB" the site shows, in bytes
    }

    [Theory]
    [InlineData("""<span class="user-screen-name hidden-sm hidden-md">demo_account</span>""", "demo_account")]
    [InlineData("""<span class="user-screen-name">  spaced  </span>""", "spaced")]
    [InlineData("""<span class="user-screen-name"></span>""", null)]
    [InlineData("<div>no header here</div>", null)]
    public void ParseScreenName_ReadsTheHeaderLabelOnly(string html, string? expected)
        => Assert.Equal(expected, FilestankPipeline.ParseScreenName(html));

    [Fact]
    public void Filestank_IsAccountOnly_OnTheSessionCookieCredentialUi()
    {
        FilestankPipeline pipeline = new();
        Assert.Equal("Filestank", pipeline.Name);
        Assert.False(pipeline.SupportsAnonymousUpload);

        // 20 GiB — the uploader's own maxFileSize (21474836480), which it renders as "20.00 GB".
        Assert.Equal(21474836480L, pipeline.MaxFileSize);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Filestank"));
        Assert.Equal("www.filestank.com", FileHosterClient.FileHosters["Filestank"]);

        // The credential is the captured cookie: there is no API key to paste and no username/password
        // the pipeline could use, because the login is reCAPTCHA-gated.
        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("Filestank"));
    }

    [Fact]
    public async Task CheckAccountAsync_WithoutTheEmbeddedBrowser_FailsWithAnActionableMessage()
    {
        FilestankPipeline pipeline = new();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", "pass", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("embedded browser", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_CancelledSignIn_IsNotAValidAccount()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InteractiveAuthResult?)null);

        FilestankPipeline pipeline = new(auth.Object);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", string.Empty, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.SessionCookie);
    }

    [Fact]
    public async Task CheckAccountAsync_WaitsForThePostLoginNavigation()
    {
        // The filehosting cookie is issued to anonymous visitors and keeps the SAME value through
        // login, so bare cookie-presence would close the window on a guest session.
        InteractiveAuthSpec? seen = null;
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .Callback<InteractiveAuthSpec, string, ProxyChoice?, CancellationToken>((s, _, _, _) => seen = s)
            .ReturnsAsync((InteractiveAuthResult?)null);

        await new FilestankPipeline(auth.Object).CheckAccountAsync(
            "user", string.Empty, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(seen!.Value.CaptureOnlyAfterLeavingLoginPage);
        Assert.Equal("filehosting", seen.Value.CookieName);
        Assert.Equal("https://www.filestank.com/account/login", seen.Value.LoginUrl);
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

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.avi",
        FileName = "x.avi",
        FileSize = 5225142,
        HosterName = "Filestank",
        Credentials = new FileHosterLoginDto
        {
            Id = 1,
            FileHosterName = "Filestank",
            SessionCookie = Session,
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(4),
        },
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
}
