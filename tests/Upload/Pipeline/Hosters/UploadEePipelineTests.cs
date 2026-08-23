// <copyright file="UploadEePipelineTests.cs" company="CSUploader">
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
/// upload.ee — the tree's only Uber-Uploader host. Fixtures are the real responses from a browser
/// capture and a live run (2026-08-05). Two things are pinned above all: that the upload id comes
/// from the SERVER (inventing one dies inside their Perl), and that BOTH redirect shapes are handled
/// — the browser is answered with a 302, this client with a 200 carrying a JS redirect.
/// </summary>
public class UploadEePipelineTests
{
    private const string IdJs = """if(typeof startUpload==='function'){startUpload("c93cc90ca1aeac83b3586aad022b9b62",0);}""";

    private const string FinishedHtml = """
        <html><body>
        <h1 class="pageTitle">File successfully uploaded!!</h1><br />
        View file:<br /><a href="https://www.upload.ee/files/19619815/csu-probe.rar.html">https://www.upload.ee/files/19619815/csu-probe.rar.html</a><br /><br />
        Delete file:<br /><a href="https://www.upload.ee/files/19619815/csu-probe.rar.html?killcode=43942159516900476294">delete</a>
        </body></html>
        """;

    // The homepage's login form, whose ___nonce the sign-in POST has to echo back.
    private const string LoginPageHtml = """
        <html><body><form action="/login.html" method="post">
        <input type="text" name="u[username]" /><input type="password" name="u[password]" />
        <input type="hidden" name="u[page]" value="" />
        <input type="hidden" name="___nonce" value="71070709_bc90ab" />
        <input type="submit" name="login" value=" Enter " /></form></body></html>
        """;

    // What the login's redirect lands on, copied from the capture's markup rather than paraphrased.
    // ⚠ The name is wrapped in <b>, and the first version of this fixture wasn't: it was written from a
    // capture-analysis script's TAG-STRIPPED text ("Welcome, csuprobe !"). That shape never crosses the
    // wire, so the greeting pattern derived from it matched nothing live while every test passed.
    private const string LandedHtml = """
        <html><body><table><tr>
        <td>Welcome, <b>csuprobe</b>!</td>
        <td><form action="https://www.upload.ee/logout.html" method="post"><input type="hidden" name="u[page]" value="" /><input type="submit" name="logout" value=" Logout " /></form></td>
        </tr></table></body></html>
        """;

    [Fact]
    public async Task RunAsync_UsesTheServersUploadId_AndReturnsTheViewLink()
    {
        List<string> gets = [];
        string? uploadUrl = null;

        UploadEePipeline pipeline = new(
            getOverride: (url, _) =>
            {
                gets.Add(url);
                return Task.FromResult(url.Contains("ubr_link_upload", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));
            },
            uploadOverride: (_, url, fields, _, _) =>
            {
                uploadUrl = url;
                Assert.Empty(fields); // the capture's POST carries the file and nothing else
                return Task.FromResult(new HttpResponseSnapshot(
                    302, string.Empty, Array.Empty<string>(),
                    "https://www.upload.ee/?page=finished&upload_id=c93cc90ca1aeac83b3586aad022b9b62"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The id must be the one the SERVER handed back — a self-invented one reaches ubr_upload.pl
        // and dies there ("could not open link file"), because the .link file is written when the
        // server issues an id.
        Assert.NotNull(uploadUrl);
        Assert.Contains("X-Progress-ID=c93cc90ca1aeac83b3586aad022b9b62", uploadUrl!, StringComparison.Ordinal);
        Assert.Contains("upload_id=c93cc90ca1aeac83b3586aad022b9b62", uploadUrl!, StringComparison.Ordinal);

        // Step 1 first, result page last.
        Assert.Contains("ubr_link_upload.php?rnd_id=", gets[0], StringComparison.Ordinal);
        Assert.Contains("page=finished", gets[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JsRedirect_IsFollowedJustLikeThe302()
    {
        // The capture (a browser) got a 302 with a Location. This client gets 200 and
        // parent.location.href='…' instead — the iframe-era redirect. Relying on either alone would
        // pass in testing and fail in the field, so both are handled.
        UploadEePipeline pipeline = new(
            getOverride: (url, _) => Task.FromResult(url.Contains("ubr_link_upload", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())
                : new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                "<html><body>UPLOAD.EE<script>parent.location.href='https://www.upload.ee/?page=finished&upload_id=c93cc90ca1aeac83b3586aad022b9b62';</script></body></html>",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenTheHandlerAlreadyFollowedTheRedirect_UsesTheBodyItHasRatherThanRefetching()
    {
        // THE SHAPE THIS APP ACTUALLY SEES, and the one the first implementation got wrong: our
        // HttpHandler follows the 302, so the upload's own response IS the finished page — 200, no
        // Location, no JS redirect. The first version discarded that, invented a ?page=finished URL and
        // re-fetched it, which upload.ee answers with its HOMEPAGE once the id has been consumed. Every
        // unit test passed and the live run failed, because none of them modelled this client.
        List<string> gets = [];
        UploadEePipeline pipeline = new(
            getOverride: (url, _) =>
            {
                gets.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, IdJs, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // Only the id request — the result page was already in hand, so no second round trip.
        Assert.Single(gets);
        Assert.Contains("ubr_link_upload.php", gets[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoUploadId_FailsWithoutSendingAnything()
    {
        // If the id step doesn't answer with one, uploading would only earn the Perl error — so don't.
        bool uploaded = false;
        UploadEePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, "/* nothing useful */", Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("upload id", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(uploaded);
    }

    [Theory]
    [InlineData(IdJs, "c93cc90ca1aeac83b3586aad022b9b62")]
    [InlineData("""if(typeof startUpload==='function'){startUpload("ABC123def456",0);}""", "ABC123def456")]
    [InlineData("/* no call at all */", null)]
    public void ParseUploadId_ReadsTheIdOutOfTheJavaScript(string body, string? expected)
    {
        (string? id, string? error) = UploadEePipeline.ParseUploadId(new HttpResponseSnapshot(200, body, Array.Empty<string>()));
        Assert.Equal(expected, id);
        Assert.Equal(expected is null, error is not null);
    }

    [Fact]
    public void ParseFinishedPage_ReadsTheLinkAndTheKillcode()
    {
        (string? url, string? delete, string? error) =
            UploadEePipeline.ParseFinishedPage(new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html", url);

        // The killcode shows once, on this page, and an anonymous upload has no account to manage the
        // file from — so it gets logged rather than dropped.
        Assert.Equal("https://www.upload.ee/files/19619815/csu-probe.rar.html?killcode=43942159516900476294", delete);
    }

    [Fact]
    public async Task RunAsync_OversizedFile_RejectedBeforeAnyTransfer()
    {
        bool touched = false;
        UploadEePipeline pipeline = new(
            getOverride: (_, _) => { touched = true; return Task.FromResult(new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())); },
            uploadOverride: (_, _, _, _, _) => { touched = true; return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext() with { FileSize = (100L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(touched);
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_SignsInOnce_AndCarriesBothSessionCookiesOnEveryStep()
    {
        // The account path is the SAME three upload steps with a session on them. Two things are pinned:
        // that the login's redirect is followed (upload_sess_sec arrives on the 302, sess_sec only on the
        // page it points at — stopping early leaves a half-session that looks signed in and isn't), and
        // that a batch signs in ONCE rather than once per file.
        List<string> gets = [];
        List<IReadOnlyDictionary<string, string>> forms = [];
        List<string?> uploadCookies = [];

        UploadEePipeline pipeline = new(
            getOverride: (url, _) =>
            {
                gets.Add(url);
                if (url.Contains("ubr_link_upload", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseSnapshot(200, IdJs, Array.Empty<string>()));
                }

                if (url.EndsWith('/'))
                {
                    return Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>()));
                }

                // The post-login landing page: the greeting, and the cookie the 302 did NOT set.
                return Task.FromResult(new HttpResponseSnapshot(
                    200, LandedHtml, ["sess_sec=b2b2b2; path=/", "lng=eng; path=/"]));
            },
            uploadOverride: (_, _, _, headers, _) =>
            {
                uploadCookies.Add(headers?.GetValueOrDefault("Cookie"));
                return Task.FromResult(new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));
            },
            postFormOverride: (url, form, _) =>
            {
                Assert.Equal("https://www.upload.ee/login.html", url);
                forms.Add(form);
                return Task.FromResult(new HttpResponseSnapshot(
                    302, string.Empty, ["upload_sess_sec=a1a1a1; path=/"], "https://www.upload.ee/?"));
            });

        AttemptContext ctx = AccountContext();
        List<UploadEvent> first = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));
        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(first.Concat(second).OfType<AttemptFailed>());
        Assert.Equal(2, first.Concat(second).OfType<TransferCompleted>().Count());

        // One login for both files — the second run reused the cached session.
        IReadOnlyDictionary<string, string> form = Assert.Single(forms);
        Assert.Equal("csuprobe", form["u[username]"]);
        Assert.Equal("hunter2", form["u[password]"]);
        Assert.Equal(string.Empty, form["u[page]"]);
        Assert.Equal("71070709_bc90ab", form["___nonce"]);
        Assert.Equal(" Enter ", form["login"]); // the submit button's value, spaces included

        // The redirect was followed, and it was the Location the host gave.
        Assert.Equal("https://www.upload.ee/", gets[0]);
        Assert.Equal("https://www.upload.ee/?", gets[1]);

        // Every upload carried BOTH cookies — the half-session is the failure this guards.
        Assert.Equal(2, uploadCookies.Count);
        foreach (string? cookie in uploadCookies)
        {
            Assert.NotNull(cookie);
            Assert.Contains("upload_sess_sec=a1a1a1", cookie!, StringComparison.Ordinal);
            Assert.Contains("sess_sec=b2b2b2", cookie!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_WhenSignInFails_NothingIsUploaded()
    {
        // A bad password re-renders the same page rather than saying so, which makes the greeting's
        // absence the only signal there is. Uploading anyway would silently produce an ANONYMOUS upload.
        bool uploaded = false;
        UploadEePipeline pipeline = new(
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));
            },
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, Array.Empty<string>(), "https://www.upload.ee/?")));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(AccountContext(), CancellationToken.None));

        Assert.Contains("username and password", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(uploaded);
    }

    // The landing page is read for two independent markers, because keying on one already broke once.
    [Theory]
    // The wire shape: the name is wrapped in markup. This is the case the shipped pattern missed.
    [InlineData(LandedHtml, true, "csuprobe")]
    // The same greeting without the markup, in case the header is ever restyled.
    [InlineData("<html><body><td>Welcome, csuprobe !</td></body></html>", true, "csuprobe")]
    // Logout control but no greeting: still signed in, so fall back to the typed name rather than fail.
    [InlineData("""<html><body><form action="https://www.upload.ee/logout.html"></form></body></html>""", true, "CSUPROBE")]
    // Neither marker — a bad password re-renders the login form rather than saying anything.
    [InlineData(LoginPageHtml, false, null)]
    public async Task CheckAccountAsync_ReadsTheSignedInMarkersOffTheLandingPage(string landed, bool valid, string? expectedName)
    {
        UploadEePipeline pipeline = new(
            getOverride: (url, _) => Task.FromResult(url.EndsWith('/')
                ? new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>())
                : new HttpResponseSnapshot(200, landed, ["sess_sec=b2b2b2"])),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload for a check"),
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["upload_sess_sec=a1a1a1"], "https://www.upload.ee/?")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "CSUPROBE", "hunter2", null,
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.Equal(valid, result.IsValid);

        // When the site names the account, that name wins over the typed one — a login that succeeds
        // against different case or an e-mail alias still shows the real account name.
        Assert.Equal(expectedName, result.DerivedUsername);
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_AllowsAFileThatIsOversizedForAnonymous()
    {
        // 200 MB signed in against 100 MB anonymous — the tier is the whole reason to sign in here.
        Assert.Equal(100L * 1024 * 1024, new UploadEePipeline().MaxFileSizeFor(
            new FileHosterLoginDto { Id = 0, FileHosterName = "Upload.ee", IsAnonymous = true }));
        Assert.Equal(200L * 1024 * 1024, new UploadEePipeline().MaxFileSizeFor(
            new FileHosterLoginDto { Id = 7, FileHosterName = "Upload.ee", IsAnonymous = false }));

        bool uploaded = false;
        UploadEePipeline pipeline = new(
            getOverride: (url, _) => Task.FromResult(url.Contains("ubr_link_upload", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, IdJs, Array.Empty<string>())
                : url.EndsWith('/')
                    ? new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, LandedHtml, ["sess_sec=b2b2b2"])),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, FinishedHtml, Array.Empty<string>()));
            },
            postFormOverride: (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["upload_sess_sec=a1a1a1"], "https://www.upload.ee/?")));

        AttemptContext ctx = AccountContext() with { FileSize = 150L * 1024 * 1024 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.True(uploaded);
    }

    [Fact]
    public void UploadEe_IsAnonymous_AndRegistered()
    {
        UploadEePipeline pipeline = new();
        Assert.Equal("Upload.ee", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(100L * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal("upload.ee", FileHosterClient.FileHosters["Upload.ee"]);
    }

    private static AttemptContext AccountContext() => MakeContext() with
    {
        Credentials = new FileHosterLoginDto
        {
            Id = 7,
            FileHosterName = "Upload.ee",
            IsAnonymous = false,
            Username = "csuprobe",
            Password = "hunter2",
        },
    };

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\csu-probe.rar",
        FileName = "csu-probe.rar",
        FileSize = 1024,
        HosterName = "Upload.ee",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "Upload.ee", IsAnonymous = true },
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
