// <copyright file="DataNodesPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Globalization;
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
/// DataNodes' xfspro variant. Every fixture below is a real response — the node lookup, the chunk
/// acknowledgement, both finalise shapes, the login form and the account page all come from browser
/// captures of an anonymous and a signed-in upload, and were re-confirmed by driving this pipeline
/// live. Session values, the account name and the killcodes are faked; nothing else is edited.
/// </summary>
public class DataNodesPipelineTests : IDisposable
{
    private const string NodeJson = """{"plugin":"xfspro","url":"https://node42.datanodes.to/cgi-bin"}""";
    private const string ChunkOkJson = """{"status":"OK"}""";

    /// <summary>Anonymous: no <c>status</c>, no <c>file_code</c>, links first.</summary>
    private const string AnonymousFinaliseJson = """
        {"links":{"download_link":"https://datanodes.to/nnqar5onpr6r/Free_Test_Data_5MB_AVI.avi","delete_link":"https://datanodes.to/nnqar5onpr6r/Free_Test_Data_5MB_AVI.avi?killcode=k1llc0dexx","html_code":"<a href=\"https://datanodes.to/nnqar5onpr6r/Free_Test_Data_5MB_AVI.avi\" target=_blank>Free_Test_Data_5MB_AVI.avi - 5.0 MB</a>"}}
        """;

    /// <summary>Signed in: <c>status</c> and <c>file_code</c> lead, and <c>download_link</c> comes
    /// LAST inside <c>links</c>. The same parser has to read both.</summary>
    private const string AccountFinaliseJson = """
        {"status":"OK","file_code":"x0ckfxsfm802","links":{"html_code":"<a href=\"https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi\" target=_blank>Free_Test_Data_5MB_AVI.avi - 5.0 MB</a>","forum_code":"[URL=https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi]Free_Test_Data_5MB_AVI.avi -  5.0 MB[/URL]","delete_link":"https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi?killcode=k1llc0dexx","download_link":"https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi"}}
        """;

    private const string LoginPageHtml = """
        <form action="https://datanodes.to/" name="FL" class="space-y-6">
            <input type="hidden" name="op" value="login">
            <input type="hidden" name="token" value="9d6ac61dca95e889c7dcb7debf08293a">
            <input type="hidden" name="rand" value="">
        </form>
        """;

    /// <summary>The signed-in account page: the plan chip, the used/quota tile and the plain
    /// <c>/logout</c> link this fork uses in place of the family's <c>op=logout</c>.</summary>
    private const string AccountPageHtml = """
        <a href="/logout" class="text-xs hover:text-white hover:bg-blue-800">Logout</a>
        <span class="text-sm font-semibold text-gray-900">Free plan</span>
        <div class="p-4">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-gray-400 m-0 mb-1">Used space</p>
            <p class="text-2xl font-bold text-gray-900 m-0 [font-variant-numeric:tabular-nums]">0.01 <span class="text-sm font-semibold text-gray-400">/ 1024 GB</span></p>
        </div>
        """;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "csu-dn-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public DataNodesPipelineTests() => Directory.CreateDirectory(_tempDir);

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
    public async Task Pipeline_IsAnonymousCapable_AndWiredIntoTheApp()
    {
        DataNodesPipeline pipeline = new();

        Assert.Equal("DataNodes", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);      // proved by uploading bytes with no account
        Assert.Equal(3L * 1024 * 1024 * 1024, pipeline.MaxFileSize);   // the page's own :max-size
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
        Assert.Null(pipeline.MaxFilesPerPackage);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("DataNodes"));
        Assert.Equal("datanodes.to", FileHosterClient.FileHosters["DataNodes"]);
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("DataNodes"));
        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);
        await Task.CompletedTask;
    }

    // ── The node lookup ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseNode_ReadsTheUrl_AndDropsATrailingSlash()
    {
        Assert.Equal(
            ("https://node42.datanodes.to/cgi-bin", null),
            DataNodesPipeline.ParseNode(new HttpResponseSnapshot(200, NodeJson, [])));

        // The endpoints are appended to this, so a trailing slash would produce "//put_chunk_mt.cgi".
        (string? node, _) = DataNodesPipeline.ParseNode(
            new HttpResponseSnapshot(200, """{"url":"https://node42.datanodes.to/cgi-bin/"}""", []));
        Assert.Equal("https://node42.datanodes.to/cgi-bin", node);
    }

    [Theory]
    [InlineData(503, NodeJson, "503")]
    [InlineData(200, """{"plugin":"xfspro"}""", "no url")]
    [InlineData(200, """{"url":""}""", "no url")]
    [InlineData(200, "<html>maintenance</html>", "wasn't JSON")]
    public void ParseNode_RefusesAnythingThatIsNotANode(int status, string body, string fragment)
    {
        (string? node, string? error) = DataNodesPipeline.ParseNode(new HttpResponseSnapshot(status, body, []));

        Assert.Null(node);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The finalise ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AnonymousFinaliseJson, "https://datanodes.to/nnqar5onpr6r/Free_Test_Data_5MB_AVI.avi")]
    [InlineData(AccountFinaliseJson, "https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi")]
    public void ParseImportResponse_ReadsBothReplyShapes(string body, string expected)
    {
        (string? link, string? delete, string? error) = DataNodesPipeline.ParseImportResponse(
            new HttpResponseSnapshot(200, body, []));

        Assert.Null(error);
        Assert.Equal(expected, link);
        Assert.Equal(expected + "?killcode=k1llc0dexx", delete);
    }

    [Fact]
    public void ParseImportResponse_TreatsTheDeleteLinkAsOptional()
    {
        (string? link, string? delete, string? error) = DataNodesPipeline.ParseImportResponse(
            new HttpResponseSnapshot(200, """{"links":{"download_link":"https://datanodes.to/abc/x.rar"}}""", []));

        Assert.Null(error);
        Assert.Equal("https://datanodes.to/abc/x.rar", link);
        Assert.Null(delete);
    }

    [Theory]
    [InlineData(500, "<html>500 Internal Server Error</html>", "refused to finalise")]
    [InlineData(200, """{"status":"OK","file_code":"x0ckfxsfm802"}""", "no link")]
    [InlineData(200, """{"links":{"delete_link":"https://datanodes.to/abc/x.rar?killcode=k"}}""", "no link")]
    [InlineData(200, """{"links":{"download_link":""}}""", "no link")]
    [InlineData(200, "<html>bad gateway</html>", "wasn't JSON")]
    public void ParseImportResponse_ExplainsEveryFailure(int status, string body, string fragment)
    {
        (string? link, string? delete, string? error) = DataNodesPipeline.ParseImportResponse(
            new HttpResponseSnapshot(status, body, []));

        Assert.Null(link);
        Assert.Null(delete);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseImportResponse_AFailedFinalise_SaysTheBytesAreNotWhatFailed()
    {
        // This host 500s here occasionally with the whole file already uploaded. The message has to
        // send the user looking at the finalise, not at their connection.
        (_, _, string? error) = DataNodesPipeline.ParseImportResponse(
            new HttpResponseSnapshot(500, "<html>500 Internal Server Error</html>", []));

        Assert.Contains("took the file", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The upload SID ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewUploadSid_IsSixteenDigits_AndNeverRepeats()
    {
        HashSet<string> seen = [];
        for (int i = 0; i < 200; i++)
        {
            string sid = DataNodesPipeline.NewUploadSid();

            Assert.Equal(16, sid.Length);
            Assert.All(sid, c => Assert.InRange(c, '0', '9'));

            // Two files uploading at once must not share one — the SID is what ties a file's chunks
            // together, so a collision would splice two uploads into one file.
            Assert.True(seen.Add(sid));
        }
    }

    // ── The session cookie ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadSessionCookie_TakesXfss_FromTheRealLoginReply()
    {
        HttpResponseSnapshot login = new(
            302,
            string.Empty,
            [
                "xfss=s3ss10nva1ue0001; domain=.datanodes.to; path='/'; HttpOnly; Max-Age=2592000",
                "xfss=s3ss10nva1ue0001; domain=.datanodes.to; path=/; expires=Tue, 08-Sep-2026 08:34:14 GMT",
                "login=csuprobe; domain=.datanodes.to; path=/; expires=Fri, 05-Feb-2027 08:34:14 GMT",
            ],
            "https://datanodes.to/");

        Assert.Equal("s3ss10nva1ue0001", DataNodesPipeline.ReadSessionCookie(login));
    }

    [Theory]
    [InlineData("login=csuprobe; path=/")]                 // a bad password sets no xfss at all
    [InlineData("xfss=; domain=.datanodes.to; path=/")]     // …and a logout empties it
    [InlineData("xfss=deleted; domain=.datanodes.to; path=/")]
    public void ReadSessionCookie_IsNullWhenNoUsableSessionWasIssued(string setCookie)
    {
        Assert.Null(DataNodesPipeline.ReadSessionCookie(new HttpResponseSnapshot(302, string.Empty, [setCookie])));
    }

    // ── The account page ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseAccountPage_ReadsThePlanAndTheStorageTile()
    {
        (bool signedIn, AccountType type, long? used, long? quota) =
            DataNodesPipeline.ParseAccountPage(new HttpResponseSnapshot(200, AccountPageHtml, []));

        Assert.True(signedIn);
        Assert.Equal(AccountType.Free, type);

        // "0.01 / 1024 GB": the used figure carries no unit of its own and is rendered in the quota's.
        Assert.Equal((long)(0.01 * (1L << 30)), used);
        Assert.Equal(1024L << 30, quota);
    }

    [Fact]
    public void ParseAccountPage_ThePlainLogoutLink_IsWhatProvesTheSession()
    {
        // The regression this pins: matching the family's op=logout made a live, working session read
        // as expired, because this fork's template links /logout plainly.
        Assert.False(DataNodesPipeline.ParseAccountPage(
            new HttpResponseSnapshot(200, AccountPageHtml.Replace("/logout", "/op=nothing", StringComparison.Ordinal), [])).SignedIn);

        Assert.True(DataNodesPipeline.ParseAccountPage(
            new HttpResponseSnapshot(200, """<a href="/logout">Logout</a>""", [])).SignedIn);
    }

    [Fact]
    public void ParseAccountPage_ADeadSession_IsBouncedToTheLoginForm()
    {
        // Exactly what a junk xfss gets back, verified live: a 302 with an empty body.
        (bool signedIn, _, long? used, long? quota) = DataNodesPipeline.ParseAccountPage(
            new HttpResponseSnapshot(302, string.Empty, [], "https://datanodes.to/login.html"));

        Assert.False(signedIn);
        Assert.Null(used);
        Assert.Null(quota);
    }

    [Fact]
    public void ParseAccountPage_ARequestThatNeverAnswered_IsNotASession()
    {
        Assert.False(DataNodesPipeline.ParseAccountPage(null).SignedIn);
    }

    [Fact]
    public void ParseAccountPage_AnErrorPage_IsNotASession_EvenCarryingTheSiteNav()
    {
        // An edge or error page can still render the signed-in navigation. The status is what says
        // whether the account page was actually served.
        Assert.False(DataNodesPipeline.ParseAccountPage(new HttpResponseSnapshot(500, AccountPageHtml, [])).SignedIn);
    }

    [Fact]
    public void ParseAccountPage_KeepsTheSession_WhenOnlyTheStorageTileIsMissing()
    {
        // A template change to the tile must cost the figures, not the account.
        (bool signedIn, AccountType type, long? used, long? quota) = DataNodesPipeline.ParseAccountPage(
            new HttpResponseSnapshot(200, """<a href="/logout">Logout</a><span class="x">Free plan</span>""", []));

        Assert.True(signedIn);
        Assert.Equal(AccountType.Free, type);
        Assert.Null(used);
        Assert.Null(quota);
    }

    [Fact]
    public void ParseAccountPage_ReportsPremium_OnlyWhenThePageSaysSo()
    {
        // Only a free account was available to probe, so premium's wording is unverified; anything
        // unrecognised has to read as free rather than be guessed upward.
        Assert.Equal(
            AccountType.Premium,
            DataNodesPipeline.ParseAccountPage(new HttpResponseSnapshot(
                200,
                AccountPageHtml.Replace("Free plan", "Premium plan", StringComparison.Ordinal),
                [])).Type);

        Assert.Equal(
            AccountType.Free,
            DataNodesPipeline.ParseAccountPage(new HttpResponseSnapshot(
                200,
                AccountPageHtml.Replace("Free plan", "Lifetime plan", StringComparison.Ordinal),
                [])).Type);
    }

    // ── Signing in ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAccount_PostsThePagesOwnToken_AndKeepsTheUsernameAsTyped()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Form)> posts = [];
        DataNodesPipeline pipeline = new(
            (url, form) =>
            {
                posts.Add((url, form));
                return Task.FromResult(new HttpResponseSnapshot(302, string.Empty, ["xfss=s3ss10nva1ue0001; path=/"], "https://datanodes.to/"));
            },
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks in a sign-in"),
            (url, _) => Task.FromResult(new HttpResponseSnapshot(200, url.EndsWith("/login", StringComparison.Ordinal) ? LoginPageHtml : AccountPageHtml, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync("csuprobe", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10nva1ue0001", result.SessionCookie);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal((long)(0.01 * (1L << 30)), result.StorageUsedBytes);
        Assert.Equal(1024L << 30, result.StorageQuotaBytes);

        // The identifier the next sign-in posts is the one the user typed; nothing scraped may
        // replace it.
        Assert.Equal("csuprobe", result.DerivedUsername);

        (string url, IReadOnlyDictionary<string, string> form) = Assert.Single(posts);
        Assert.Equal("https://datanodes.to/", url);
        Assert.Equal("login", form["op"]);
        Assert.Equal("9d6ac61dca95e889c7dcb7debf08293a", form["token"]);   // scraped, not invented
        Assert.Equal("csuprobe", form["login"]);
        Assert.Equal("pw", form["password"]);
    }

    [Fact]
    public async Task CheckAccount_AWrongPassword_IsTheAbsenceOfASessionCookie()
    {
        // The family answers a bad password by re-rendering the form, not with an error, so there is
        // nothing else to read.
        DataNodesPipeline pipeline = new(
            (_, _) => Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, [])),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (_, _) => Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, [])));

        AccountCheckResult result = await pipeline.CheckAccountAsync("csuprobe", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("csuprobe", "")]
    public async Task CheckAccount_WithoutBothHalvesOfTheCredential_AsksForThem_WithoutCallingTheHost(string user, string password)
    {
        DataNodesPipeline pipeline = new(
            (_, _) => throw new InvalidOperationException("must not post"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("must not chunk"),
            (_, _) => throw new InvalidOperationException("must not get"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(user, password, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username and password", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_ASessionTheAccountPageRejects_IsNotAnAccount()
    {
        // A cookie that is already worthless has to fail here, not at the first upload.
        DataNodesPipeline pipeline = new(
            (_, _) => Task.FromResult(new HttpResponseSnapshot(302, string.Empty, ["xfss=s3ss10nva1ue0001; path=/"], "https://datanodes.to/")),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (url, _) => Task.FromResult(url.EndsWith("/login", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, LoginPageHtml, [])
                : new HttpResponseSnapshot(302, string.Empty, [], "https://datanodes.to/login.html")));

        AccountCheckResult result = await pipeline.CheckAccountAsync("csuprobe", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("account page", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccount_AnAccountPageThatNeverAnswered_StillKeepsTheProvenSession()
    {
        // The cookie is what proves the password; a blip fetching the stats page must not throw the
        // sign-in away.
        DataNodesPipeline pipeline = new(
            (_, _) => Task.FromResult(new HttpResponseSnapshot(302, string.Empty, ["xfss=s3ss10nva1ue0001; path=/"], "https://datanodes.to/")),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (url, _) => url.EndsWith("/login", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, []))
                : throw new HttpRequestException("connection reset"));

        AccountCheckResult result = await pipeline.CheckAccountAsync("csuprobe", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10nva1ue0001", result.SessionCookie);
        Assert.Null(result.StorageUsedBytes);
    }

    // ── Re-checking a stored session ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAccount_SendsTheStoredCookie_AndReportsTheStorageItFinds()
    {
        List<IReadOnlyDictionary<string, string>> gets = [];
        DataNodesPipeline pipeline = new(
            (_, _) => throw new InvalidOperationException("a refresh must not need the password"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (_, headers) =>
            {
                gets.Add(headers);
                return Task.FromResult(new HttpResponseSnapshot(200, AccountPageHtml, []));
            });

        AccountCheckResult result = await pipeline.RefreshAccountAsync(null, "s3ss10nva1ue0001", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("s3ss10nva1ue0001", result.SessionCookie);
        Assert.Equal(1024L << 30, result.StorageQuotaBytes);

        // Without this header the page is just the visitor's, and the check would pass or fail on the
        // wrong evidence.
        Assert.Equal("xfss=s3ss10nva1ue0001", Assert.Single(gets)["Cookie"]);

        // A refresh has no username to offer, and must not overwrite the stored one with a blank.
        Assert.Null(result.DerivedUsername);
    }

    [Fact]
    public async Task RefreshAccount_AnExpiredSession_AsksForAFreshSignIn()
    {
        DataNodesPipeline pipeline = new(
            (_, _) => throw new InvalidOperationException("no posts"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (_, _) => Task.FromResult(new HttpResponseSnapshot(302, string.Empty, [], "https://datanodes.to/login.html")));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(null, "expired", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("sign in again", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccount_AHostThatIsDown_DoesNotClaimTheSessionIsGood()
    {
        DataNodesPipeline pipeline = new(
            (_, _) => throw new InvalidOperationException("no posts"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("no chunks"),
            (_, _) => throw new HttpRequestException("no such host"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(null, "s3ss10nva1ue0001", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    // ── Uploading ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Anonymously_LooksUpANode_SendsTheFile_AndFinalisesWithAnEmptySession()
    {
        Recorder recorder = new(AnonymousFinaliseJson);
        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder, anonymous: true), CancellationToken.None));

        Assert.Equal("https://datanodes.to/nnqar5onpr6r/Free_Test_Data_5MB_AVI.avi", Assert.IsType<TransferCompleted>(events[^1]).FileUrl);

        (string startUrl, IReadOnlyDictionary<string, string> start) = recorder.Posts[0];
        Assert.Equal("https://datanodes.to/", startUrl);
        Assert.Equal("start_upload", start["op"]);
        Assert.Equal("release.r00", start["file_name"]);
        Assert.Equal("4096", start["file_size"]);
        Assert.Equal("1", start["file_public"]);

        (string finaliseUrl, IReadOnlyDictionary<string, string> finalise) = recorder.Posts[1];
        Assert.Equal("https://node42.datanodes.to/cgi-bin/api.cgi", finaliseUrl);
        Assert.Equal("import_file", finalise["op"]);
        Assert.Equal("release.r00", finalise["fname"]);

        // The one field that separates a guest upload from an account's.
        Assert.Equal(string.Empty, finalise["sess_id"]);
        Assert.Equal(finalise["sid"], recorder.ChunkHeaders[0]["X-Upload-SID"]);
    }

    [Fact]
    public async Task Run_SignedIn_UploadsUnderTheStoredSession_WithoutAskingForThePassword()
    {
        Recorder recorder = new(AccountFinaliseJson);
        AttemptContext ctx = MakeContext(recorder, anonymous: false, sessionCookie: "s3ss10nva1ue0001");

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal("https://datanodes.to/x0ckfxsfm802/Free_Test_Data_5MB_AVI.avi", Assert.IsType<TransferCompleted>(events[^1]).FileUrl);
        Assert.Equal("s3ss10nva1ue0001", recorder.Posts[1].Form["sess_id"]);

        // A stored session means no login page and no credential POST — only the two upload calls.
        Assert.Equal(2, recorder.Posts.Count);
        Assert.Empty(recorder.Gets);
    }

    [Fact]
    public async Task Run_SignedIn_WithNoStoredSession_SignsInFirst()
    {
        Recorder recorder = new(AccountFinaliseJson) { LoginSetsCookie = "xfss=fr3shs3ss10n0001; path=/" };
        AttemptContext ctx = MakeContext(recorder, anonymous: false, sessionCookie: null);

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal("https://datanodes.to/login", Assert.Single(recorder.Gets).Url);
        Assert.Equal("login", recorder.Posts[0].Form["op"]);
        Assert.Equal("fr3shs3ss10n0001", recorder.Posts[2].Form["sess_id"]);
    }

    [Fact]
    public async Task Run_SignedIn_WhenTheSignInFails_UploadsNothing()
    {
        // Falling back to an anonymous upload would silently file the user's release under no
        // account, which is worse than failing.
        Recorder recorder = new(AccountFinaliseJson) { LoginSetsCookie = null };
        AttemptContext ctx = MakeContext(recorder, anonymous: false, sessionCookie: null);

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("username and password", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.ChunkHeaders);
    }

    [Fact]
    public async Task Run_SendsEveryChunkAtItsOwnOffset_UnderOneSid()
    {
        // A single-chunk upload cannot show this: the offsets, the short final chunk and the shared
        // SID are the whole of the multi-chunk protocol.
        Recorder recorder = new(AnonymousFinaliseJson);
        const long Size = (Recorder.ChunkSizeBytes * 2L) + 512;   // two full chunks and a remainder

        await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder, anonymous: true, fileSize: Size), CancellationToken.None));

        Assert.Equal(3, recorder.ChunkHeaders.Count);
        Assert.Equal(
            ["0", Recorder.ChunkSizeBytes.ToString(CultureInfo.InvariantCulture), (Recorder.ChunkSizeBytes * 2).ToString(CultureInfo.InvariantCulture)],
            recorder.ChunkHeaders.Select(h => h["X-Seek-To"]).OrderBy(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray());
        Assert.Equal(
            [Recorder.ChunkSizeBytes, Recorder.ChunkSizeBytes, 512L],
            recorder.ChunkLengths.OrderByDescending(v => v).ToArray());
        Assert.Single(recorder.ChunkHeaders.Select(h => h["X-Upload-SID"]).Distinct());

        // No cookie belongs on the node: this host issues none, and the session travels in the
        // finalise form instead.
        Assert.All(recorder.ChunkHeaders, h => Assert.False(h.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task Run_AChunkTheHostRefuses_NamesWhichOne_AndDoesNotFinalise()
    {
        Recorder recorder = new(AnonymousFinaliseJson) { FailChunkAtIndex = 1 };

        List<UploadEvent> events = await DrainAsync(
            recorder.Pipeline.RunAsync(MakeContext(recorder, anonymous: true, fileSize: (Recorder.ChunkSizeBytes * 2L) + 512), CancellationToken.None));

        Assert.Contains("chunk 2/3", Assert.IsType<AttemptFailed>(events[^1]).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(recorder.Posts);   // the node lookup only — nothing was finalised
    }

    [Fact]
    public async Task Run_ANodeLookupThatFails_StopsBeforeSendingAnyBytes()
    {
        Recorder recorder = new(AnonymousFinaliseJson) { NodeResponse = new HttpResponseSnapshot(503, "<html>busy</html>", []) };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder, anonymous: true), CancellationToken.None));

        Assert.Contains("503", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.Ordinal);
        Assert.Empty(recorder.ChunkHeaders);
    }

    [Fact]
    public async Task Run_AFileOverTheCap_IsRefusedWithoutTouchingTheHost()
    {
        Recorder recorder = new(AnonymousFinaliseJson);

        List<UploadEvent> events = await DrainAsync(
            recorder.Pipeline.RunAsync(MakeContext(recorder, anonymous: true, fileSize: (3L * 1024 * 1024 * 1024) + 1), CancellationToken.None));

        Assert.Contains("3 GiB", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Posts);
        Assert.Empty(recorder.ChunkHeaders);
    }

    [Fact]
    public async Task Run_AFileExactlyAtTheCap_PassesTheSizeGate()
    {
        // The cap is inclusive — the host's own uploader accepts a file of exactly :max-size — so an
        // off-by-one here would silently skip files the server would have taken.
        //
        // Asserted as a GATE rather than a transfer: the pipeline reads real slices now, and no test
        // can stage three real gigabytes to prove a comparison. The rejection message is what the
        // gate emits, so its ABSENCE at exactly the cap is the off-by-one check.
        Recorder recorder = new(AnonymousFinaliseJson);

        // Only the FIRST event: the gate rejects before anything else happens, so this settles the
        // comparison without the run having to proceed into an upload nobody can stage.
        UploadEvent? first = null;
        await foreach (UploadEvent e in recorder.Pipeline.RunAsync(
            MakeContext(recorder, anonymous: true, fileSize: 3L * 1024 * 1024 * 1024), CancellationToken.None))
        {
            first = e;
            break;
        }

        // Positively: the gate ACCEPTED it and the upload began. Asserting merely that the first
        // event is not a size failure would pass for any unrelated failure too.
        Assert.IsType<TransferStarted>(first);
    }

    [Fact]
    public async Task Run_LogsTheDeleteLink_BecauseAGuestHasNoOtherHandleOnTheFile()
    {
        Mock<IAppLogger> logger = new();
        Recorder recorder = new(AnonymousFinaliseJson);
        AttemptContext ctx = MakeContext(recorder, anonymous: true) with { Logger = logger.Object };

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        // The caller-info arguments have to be matched explicitly: leaving them to their defaults
        // would demand this test's own file and line number, and the verify would never match.
        logger.Verify(
            l => l.Log(
                It.IsAny<object>(),
                It.IsAny<LogType>(),
                It.Is<string>(m => m.Contains("killcode=k1llc0dexx", StringComparison.Ordinal)),
                It.IsAny<HttpTransaction?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()),
            Times.Once);
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

    private AttemptContext MakeContext(Recorder recorder, bool anonymous, long fileSize = 4096, string? sessionCookie = null)
    {
        // The pipeline opens the file and reads REAL slices of it before the chunk stub takes over,
        // so the bytes on disk have to match the declared size — a test that lies about it now gets
        // an out-of-range slice rather than silently reading nothing.
        //
        // Except for the cap-rejection cases, which declare gigabytes deliberately and never reach
        // the upload: allocating those for real would exhaust memory to prove a size check.
        string path = Path.Combine(_tempDir, $"release-{Guid.NewGuid():N}.r00");
        const long MaxRealBytes = 1024 * 1024;
        long realBytes = fileSize <= MaxRealBytes ? fileSize : 0;

        byte[] content = new byte[realBytes];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(path, content);

        return new AttemptContext
        {
            AttemptId = Guid.NewGuid(),
            FilePath = path,
            FileName = "release.r00",
            FileSize = fileSize,
            HosterName = "DataNodes",
            Credentials = new FileHosterLoginDto
            {
                Id = 0,
                FileHosterName = "DataNodes",
                IsAnonymous = anonymous,
                Username = "csuprobe",
                Password = "pw",
                SessionCookie = sessionCookie,
            },
            Proxy = ProxyChoice.Direct,
            Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            Logger = Mock.Of<IAppLogger>(),
            SpeedBudget = SpeedBudget.Unlimited,
            Cancellation = default,
        };
    }

    /// <summary>Stands in for the host and records what it was sent.</summary>
    private sealed class Recorder
    {
        private readonly string _finaliseJson;

        /// <summary>A small chunk so a test can declare a size it can actually write to disk;
        /// production uses 8 MiB.</summary>
        internal const int ChunkSizeBytes = 2048;

        public Recorder(string finaliseJson)
        {
            _finaliseJson = finaliseJson;
            Pipeline = new DataNodesPipeline(PostAsync, ChunkAsync, GetAsync, ChunkSizeBytes);
        }

        public DataNodesPipeline Pipeline { get; }

        public List<(string Url, IReadOnlyDictionary<string, string> Form)> Posts { get; } = [];

        public List<(string Url, IReadOnlyDictionary<string, string> Headers)> Gets { get; } = [];

        public List<IReadOnlyDictionary<string, string>> ChunkHeaders { get; } = [];

        public List<long> ChunkLengths { get; } = [];

        public HttpResponseSnapshot NodeResponse { get; init; } = new(200, NodeJson, []);

        public string? LoginSetsCookie { get; init; } = "xfss=s3ss10nva1ue0001; path=/";

        public int? FailChunkAtIndex { get; init; }

        private Task<HttpResponseSnapshot> PostAsync(string url, IReadOnlyDictionary<string, string> form)
        {
            Posts.Add((url, form));

            return Task.FromResult(form["op"] switch
            {
                "login" => new HttpResponseSnapshot(302, string.Empty, LoginSetsCookie is null ? [] : [LoginSetsCookie], "https://datanodes.to/"),
                "start_upload" => NodeResponse,
                _ => new HttpResponseSnapshot(200, _finaliseJson, []),
            });
        }

        private Task<HttpResponseSnapshot> GetAsync(string url, IReadOnlyDictionary<string, string> headers)
        {
            Gets.Add((url, headers));
            return Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, []));
        }

        private Task<HttpResponseSnapshot> ChunkAsync(string url, IReadOnlyDictionary<string, string> headers, long length, Stream body, Action<long> report, CancellationToken ct)
        {
            Assert.Equal("https://node42.datanodes.to/cgi-bin/put_chunk_mt.cgi", url);
            ChunkHeaders.Add(headers);
            ChunkLengths.Add(length);

            return Task.FromResult(ChunkHeaders.Count - 1 == FailChunkAtIndex
                ? new HttpResponseSnapshot(413, "Request Entity Too Large", [])
                : new HttpResponseSnapshot(200, ChunkOkJson, []));
        }
    }
}
