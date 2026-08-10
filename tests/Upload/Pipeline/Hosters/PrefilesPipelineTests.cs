// <copyright file="PrefilesPipelineTests.cs" company="CSUploader">
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
/// PreFiles — a thin shim on <see cref="XFileSharingApiPipeline"/> whose deviations are all in the
/// same place: this fork rewrote its routes, so the login pages moved and the account page with them,
/// and its header greets the user in a way the family's name scrape misreads. Fixtures are real
/// responses.
/// </summary>
public class PrefilesPipelineTests
{
    /// <summary>The real signed-in header. ⚠ Note what sits after the icon: the greeting, not the name
    /// — and the name in the anchor is a DISPLAY name, while the account signs in with an email.</summary>
    private const string AccountPageHtml = """
        <ul class="list-inline">
          <li><i class="fa fa-user pr-5 pl-10"></i>Hi, <a href="https://prefiles.com/my-account">Lynford Audie</a>!</li>
          <li><a href="https://prefiles.com/logout">Logout</a></li>
        </ul>
        """;

    private const string UploadFormHtml = """
        <form id="uploadfile" action="https://de7.prefiles.com/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="2mu3mzq9zr30py7c">
          <input type="hidden" name="utype" value="reg">
        </form>
        """;

    private const string UploadOkJson = """[{"file_status":"OK","file_code":"3mqt96g29ddu"}]""";

    [Fact]
    public void Prefiles_IsAccountOnly_OnTheWebFormPath()
    {
        PrefilesPipeline pipeline = new();

        Assert.Equal("PreFiles", pipeline.Name);

        // Measured at the node, not read off a page: its guest field set earns "uploads are not
        // enabled for your account type" — even though ?op=api_get_limits answers a signed-out
        // caller with a node and a cap, which on UpZur and BtaFile meant the opposite.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.Equal(512L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));
        Assert.True(pipeline.UsesWebFormUploadForTests);
        Assert.True(pipeline.SupportsDirectLoginForTests);

        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("PreFiles"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("PreFiles"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("PreFiles"));
        Assert.Equal("prefiles.com", FileHosterClient.FileHosters["PreFiles"]);
    }

    [Fact]
    public void TheLoginRoutesMoveTogether()
    {
        // This fork rewrote its routes: the family's /login.html isn't where the form is, and the
        // credential POST doesn't go to the site root either. Getting one right and not the other
        // fails at a different step each time.
        (string page, string post) = new PrefilesPipeline().LoginRoutesForTests;

        Assert.Equal("https://prefiles.com/login", page);
        Assert.Equal("https://prefiles.com/login", post);
    }

    [Fact]
    public async Task CheckAccount_ReadsTheForksOwnAccountPage_AndItsPlainLogoutLink()
    {
        // Both halves of a real failure: pointed at the family's ?op=my_files this reported "signed
        // in, but the account page didn't load as logged-in" after a sign-in that had just worked —
        // and even on the right page, the stock probe looks for ?op=logout while this fork links
        // /logout.
        List<string> gets = [];
        PrefilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(AccountPageHtml); },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["xfss=sess-from-login; path=/"], "https://prefiles.com/")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("sess-from-login", result.SessionCookie);
        Assert.Contains(gets, g => g.Contains("/my-account", StringComparison.Ordinal));
        Assert.DoesNotContain(gets, g => g.Contains("op=my_files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAccount_KeepsTheEmailTyped_RatherThanTheNameOnThePage()
    {
        // What gets stored here is what the next sign-in POSTs, and this host signs in with the
        // EMAIL. The page offers two other candidates, and both are wrong: the family's fa-user
        // anchor takes the greeting ("Hi"), and the link beside it is a display name with a space in
        // it. Either would quietly stop the account working.
        PrefilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(AccountPageHtml),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("no upload during a check"),
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                302, string.Empty, ["xfss=sess-from-login; path=/"], "https://prefiles.com/")));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Equal("csuprobe@example.test", result.DerivedUsername);
        Assert.NotEqual("Hi", result.DerivedUsername);
        Assert.NotEqual("Lynford Audie", result.DerivedUsername);
    }

    [Fact]
    public async Task RunAsync_WithAnAccount_ScrapesTheFormAndPostsToItsNode()
    {
        List<string> gets = [];
        List<UploadCall> calls = [];
        PrefilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { gets.Add(url); return Task.FromResult(UploadFormHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://prefiles.com/3mqt96g29ddu", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The family's own upload page still works on this fork — only the login moved.
        Assert.Contains(gets, g => g.Contains("op=upload_form", StringComparison.Ordinal));

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://de7.prefiles.com/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        Assert.Equal("2mu3mzq9zr30py7c", call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_AFileOverTheCap_IsRejectedWithoutAnyHttp()
    {
        bool touched = false;
        PrefilesPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { touched = true; return Task.FromResult(UploadFormHtml); },
            uploadOverride: (_, _, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeAccountContext() with { FileSize = (512L * 1024 * 1024) + 1 };

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
        HosterName = "PreFiles",
        Credentials = new FileHosterLoginDto
        {
            Id = 3,
            FileHosterName = "PreFiles",
            IsAnonymous = false,
            Username = "csuprobe@example.test",
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
