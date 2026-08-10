// <copyright file="SubysharePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Text.RegularExpressions;
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
/// SubyShare — an older XFileSharing whose upload deviates from the family in three places at once:
/// the form action arrives half-built, the field set is its own, and the reply is HTML. Fixtures are
/// the real responses, trimmed.
/// </summary>
public class SubysharePipelineTests
{
    /// <summary>The real file form. ⚠ Note <c>upload_id=</c> with nothing after it — the page's own
    /// script appends the id — and the two hidden fields the family doesn't have.</summary>
    private const string UploadFormHtml = """
        <form name="file" class="normalfile" enctype="multipart/form-data" action="https://sbs280.sbsf.tech/cgi-bin/upload.cgi?upload_id=" method="post" onsubmit="return StartUpload(this);">
          <input type="hidden" name="sess_id" value="gb14ot1mhitsfyj7">
          <input type="hidden" name="usr_id" value="571101">
          <input type="hidden" name="srv_tmp_url" value="https://sbs280.sbsf.tech/tmp">
          <input type="file" name="file_0">
        </form>
        """;

    /// <summary>The real reply — a self-submitting form, not JSON.</summary>
    private const string UploadOkHtml =
        "<HTML><BODY><div style='display:none;'><Form name='F1' action='https://subyshare.com/' target='_parent' method='POST'>"
        + "<textarea name='fn'>0b2s3zsxvf16</textarea><textarea name='st'>OK</textarea><textarea name='op'>upload_result</textarea>"
        + "</Form><Script>document.location='javascript:false';document.F1.submit();</Script></div></BODY></HTML>";

    /// <summary>The real signed-in nav. It links <c>/account/logout</c>, and carries no name
    /// anywhere.</summary>
    private const string AccountPageHtml = """
        <ul class="nav">
          <li><a href="https://subyshare.com/account/profile">My Account</a></li>
          <li class="dropdown"><a href="https://subyshare.com/filemanager">My Files</a>
            <ul class="dropdown-menu"><li><a href="/?op=my_files">Basic Mode</a></li></ul>
          </li>
          <li class="active2"><a href="https://subyshare.com/account/logout">Logout</a></li>
        </ul>
        """;

    [Fact]
    public void Subyshare_IsAccountOnly_At5Gb_OnTheWebFormPath()
    {
        SubysharePipeline pipeline = new();

        Assert.Equal("SubyShare", pipeline.Name);

        // Account-only — its uploader sits behind the sign-in and no guest form is offered. But a
        // FREE account uploads: the candidate list's "premium only" was wrong, and this cap is the
        // figure that account's own upload page states.
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.Equal(5120L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("SubyShare"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("SubyShare"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("SubyShare"));
        Assert.Equal("subyshare.com", FileHosterClient.FileHosters["SubyShare"]);
    }

    [Fact]
    public void TheForksThreeRoutes_AreItsOwn_NotTheFamilyDefaults()
    {
        // Each one fails somewhere different when wrong: the login page opens on nothing, the upload
        // reads a marketing page, and the account check reports a good session as a failed sign-in.
        (string login, string upload, string account) = new SubysharePipeline().RoutesForTests;

        Assert.Equal("https://subyshare.com/account/login", login);
        Assert.Equal("https://subyshare.com/upload", upload);
        Assert.Equal("https://subyshare.com/filemanager", account);
    }

    [Theory]
    [InlineData("""<a href="https://subyshare.com/account/logout">Logout</a>""", true)]
    [InlineData("""<a href="/?op=logout">Logout</a>""", true)]
    [InlineData("""<a href="https://subyshare.com/account/login">Login</a>""", false)]
    public void LooksSignedIn_AcceptsThisForksLogoutLink(string html, bool expected)
    {
        // This fork links /account/logout. The family's probe looks for ?op=logout only, which would
        // read a perfectly good session as a failed sign-in — the same trap DDownload, DataNodes and
        // PreFiles set.
        Assert.Equal(expected, new SubysharePipeline().LooksSignedInForTests(html));
    }

    [Fact]
    public async Task CheckAccount_KeepsTheLoginNameTyped_BecauseThePageCarriesNone()
    {
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["xfss=sess-from-login; path=/"], "https://subyshare.com/premium")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("sess-from-login", result.SessionCookie);
        Assert.Equal("csuprobe", result.DerivedUsername);
    }

    [Fact]
    public async Task RunAsync_AppendsTheUploadIdTheFormsOwnScriptWouldHave()
    {
        List<UploadCall> calls = [];
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://subyshare.com/0b2s3zsxvf16", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        UploadCall call = Assert.Single(calls);

        // The scraped action ends "upload_id=" with nothing after it. Posting it verbatim — the
        // family default — would send the whole file to a request with no upload id at all.
        Assert.DoesNotContain("upload_id=&", call.Endpoint, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"^https://sbs280\.sbsf\.tech/cgi-bin/upload\.cgi\?upload_id=\d{12}&js_on=1&utype=reg&upload_type=file&usr_id=571101$"),
            call.Endpoint);
    }

    [Fact]
    public async Task RunAsync_PostsThisForksFieldSet_IncludingTheAccountAndNodeFields()
    {
        List<UploadCall> calls = [];
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkHtml, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        IReadOnlyDictionary<string, string> fields = Assert.Single(calls).ExtraFields;
        Assert.Equal("file", fields["upload_type"]);
        Assert.Equal("gb14ot1mhitsfyj7", fields["sess_id"]);
        Assert.Equal("571101", fields["usr_id"]);
        Assert.Equal("https://sbs280.sbsf.tech/tmp", fields["srv_tmp_url"]);
        Assert.Equal("Upload!", fields["submit_btn"]);

        // utype rides the QUERY on this fork, and the modern family's fields aren't in its form at
        // all — the XFileSharing multipart parser is field-presence sensitive, so replicating the
        // proven set means replicating the absences too.
        Assert.DoesNotContain("utype", fields.Keys);
        Assert.DoesNotContain("file_public", fields.Keys);
        Assert.DoesNotContain("keepalive", fields.Keys);
    }

    [Theory]
    [InlineData("<html><body>Sign up to upload</body></html>")]
    [InlineData("""
        <form name="file" enctype="multipart/form-data" action="https://sbs280.sbsf.tech/cgi-bin/upload.cgi?upload_id=" method="post">
          <input type="hidden" name="sess_id" value="gb14ot1mhitsfyj7">
          <input type="file" name="file_0">
        </form>
        """)]
    public async Task RunAsync_AnUploadPageMissingWhatItNeeds_IsReportedAsAnExpiredSession(string html)
    {
        // Two separate refusals, and the second is the one that matters: a page WITH a form action
        // but WITHOUT usr_id still can't be uploaded to, because XFileSharing decides who a file
        // belongs to from the fields and does not complain about a field it merely disagrees with.
        // Posting anyway would file 5 GB under nobody.
        bool uploaded = false;
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(html),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.False(uploaded);
        Assert.NotEmpty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_AnUndefCodeIsAFailure_NotALink()
    {
        // The success-shaped failure this family specialises in: status OK, code "undef" — the node
        // took the bytes and threw them away. Translating the reply rather than reading it here is
        // what keeps that judgement in the one place that knows about it.
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                "<HTML><BODY><textarea name='fn'>undef</textarea><textarea name='st'>OK</textarea></BODY></HTML>",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.NotEmpty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_AnErrorStatusIsReported_NotSwallowed()
    {
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadFormHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                "<HTML><BODY><textarea name='fn'></textarea><textarea name='st'>File is too big</textarea></BODY></HTML>",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains(events.OfType<AttemptFailed>(), f => f.Reason.Contains("too big", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeUploadResponse_LeavesTheModernShapeAlone()
    {
        const string Json = """[{"file_status":"OK","file_code":"3mqt96g29ddu"}]""";

        HttpResponseSnapshot result = new SubysharePipeline().NormalizeForTests(
            new HttpResponseSnapshot(200, Json, Array.Empty<string>()));

        Assert.Equal(Json, result.Body);
    }

    [Fact]
    public void NormalizeUploadResponse_PassesAnUnrecognisedReplyThrough_SoTheParserCanReportIt()
    {
        // A Cloudflare page or an nginx error is neither shape. Inventing a status for it would turn
        // "the host said something we don't understand" into a definite verdict.
        const string Body = "<html><head><title>502 Bad Gateway</title></head></html>";

        HttpResponseSnapshot result = new SubysharePipeline().NormalizeForTests(
            new HttpResponseSnapshot(502, Body, Array.Empty<string>()));

        Assert.Equal(Body, result.Body);
        Assert.Equal(502, result.StatusCode);
    }

    [Fact]
    public async Task TwoConcurrentUploads_EachPostTheirOwnAccountAndNode()
    {
        // The reason the page's fields are keyed by attempt rather than held in a field: a pipeline
        // is a SINGLETON. Interleaved (resolve A, resolve B, upload A, upload B), plain fields would
        // hand A's POST B's usr_id — filing the upload under the wrong account, which this family
        // does silently.
        SubysharePipeline pipeline = new();
        AttemptContext a = MakeAccountContext();
        AttemptContext b = MakeAccountContext();

        await pipeline.ResolveForTests(a, UploadFormHtml);
        await pipeline.ResolveForTests(b, UploadFormHtml.Replace("571101", "999002", StringComparison.Ordinal)
            .Replace("sbs280", "sbs311", StringComparison.Ordinal));

        Dictionary<string, string> fieldsA = pipeline.BuildFieldsForTests(a, "sess-a");
        Dictionary<string, string> fieldsB = pipeline.BuildFieldsForTests(b, "sess-b");

        Assert.Equal("571101", fieldsA["usr_id"]);
        Assert.Equal("https://sbs280.sbsf.tech/tmp", fieldsA["srv_tmp_url"]);
        Assert.Equal("999002", fieldsB["usr_id"]);
        Assert.Equal("https://sbs311.sbsf.tech/tmp", fieldsB["srv_tmp_url"]);
    }

    [Fact]
    public void BuildingFieldsWithoutAResolvedPage_FailsLoudly()
    {
        // Unreachable by design, and deliberately not a silent fallback: an upload posted without
        // usr_id is the case where the node takes the bytes and files them under nobody.
        SubysharePipeline pipeline = new();

        Assert.Throws<InvalidOperationException>(() => pipeline.BuildFieldsForTests(MakeAccountContext(), "sess"));
    }

    [Fact]
    public void EveryUploadGetsItsOwnUploadId()
    {
        // It names the node's staging slot, so a shared one would have two files writing to it.
        HashSet<string> ids = [];
        for (int i = 0; i < 200; i++)
        {
            ids.Add(SubysharePipeline.NewUploadId());
        }

        Assert.Equal(200, ids.Count);
        Assert.All(ids, id => Assert.Matches(new Regex(@"^\d{12}$"), id));
    }

    [Fact]
    public async Task RunAsync_AFileOverTheCap_IsRejectedWithoutAnyHttp()
    {
        bool touched = false;
        SubysharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(UploadFormHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkHtml, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeAccountContext() with { FileSize = (5120L * 1024 * 1024) + 1 };

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

    private static AttemptContext MakeAccountContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.rar",
        FileName = "x.rar",
        FileSize = 4096,
        HosterName = "SubyShare",
        Credentials = new FileHosterLoginDto
        {
            Id = 4,
            FileHosterName = "SubyShare",
            IsAnonymous = false,
            Username = "csuprobe",
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
