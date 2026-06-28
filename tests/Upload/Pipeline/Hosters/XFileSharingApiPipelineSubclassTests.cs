// <copyright file="XFileSharingApiPipelineSubclassTests.cs" company="CSUploader">
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
/// Sanity test for the abstract <see cref="XFileSharingApiPipeline"/> base class. Locks
/// in the contract that adding a new XFileSharing-API hoster only requires supplying a
/// Name + Host — everything else (regexes, multipart shape, my_account scrape, API
/// endpoints, cookie defaults) is shared verbatim. If this test ever breaks, the base
/// has grown a new abstract / required override that needs documenting before any
/// subclass beyond ExLoadPipeline can be added.
/// </summary>
public class XFileSharingApiPipelineSubclassTests
{
    private const string MyAccountWithApiKeyHtml = """
        <!doctype html><html><body>
        <form method="POST">
          <input type="hidden" name="op" value="my_account">
          <input type="hidden" name="token" value="csrf">
          <input type="text" readonly name="api-url" value="https://test-xfs.example/api/account/info?key=demo_key">
        </form>
        </body></html>
        """;

    private const string UploadServerOkJson = """{"msg":"OK","status":200,"sess_id":"sess_demo","result":"http://fs1.test-xfs.example/cgi-bin/upload.cgi"}""";

    private const string UploadOkJson = """[{"file_code":"demoCode","file_status":"OK"}]""";

    /// <summary>
    /// Stand-in subclass for a hypothetical "TestXfsHost" hoster. Supplies only Name +
    /// Host; all behaviour comes from the base. Exercising the full RunAsync flow proves
    /// the base really is hoster-agnostic.
    /// </summary>
    private sealed class TestXfsHostPipeline : XFileSharingApiPipeline
    {
        public TestXfsHostPipeline(
            IInteractiveAuthService? authService,
            FileHosterLoginRepository? loginRepository,
            Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
            Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
            : base(authService, loginRepository, getOverride, uploadOverride)
        {
        }

        public override string Name => "TestXfsHost";

        protected override string Host => "https://test-xfs.example";
    }

    /// <summary>Like <see cref="TestXfsHostPipeline"/> but opts INTO the https→http upload-URL
    /// downgrade (the FlashBit-shape bad-cert workaround). Proves the opt-in still works.</summary>
    private sealed class DowngradingXfsHostPipeline : XFileSharingApiPipeline
    {
        public DowngradingXfsHostPipeline(
            IInteractiveAuthService? authService,
            FileHosterLoginRepository? loginRepository,
            Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
            Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
            : base(authService, loginRepository, getOverride, uploadOverride)
        {
        }

        public override string Name => "DowngradingXfsHost";

        protected override string Host => "https://test-xfs.example";

        protected override bool DowngradeUploadServerToHttp => true;
    }

    /// <summary>A cf_clearance-mode subclass (TakeFile shape): behind a Cloudflare managed challenge,
    /// so it captures cf_clearance and pins the sign-in UA.</summary>
    private sealed class CloudflareXfsHostPipeline : XFileSharingApiPipeline
    {
        public CloudflareXfsHostPipeline(
            IInteractiveAuthService? authService,
            FileHosterLoginRepository? loginRepository,
            Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
            Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
            : base(authService, loginRepository, getOverride, uploadOverride)
        {
        }

        public override string Name => "CfXfsHost";

        protected override string Host => "https://test-xfs.example";

        protected override bool RequiresCloudflareClearance => true;

        protected override string? SignInUserAgentOverride => "UA-TEST/1.0";
    }

    [Fact]
    public async Task CloudflareClearance_SignIn_ForwardsBothCookies_AndPinsSpecCookieAndUserAgent()
    {
        // TakeFile shape: the WebView captures xfss AND cf_clearance; the C# my_account scrape must
        // forward BOTH (otherwise Cloudflare serves the "Just a moment…" page), and the sign-in spec
        // must request cf_clearance + pin the UA so the captured clearance is reusable from C#.
        List<IReadOnlyDictionary<string, string>?> seenHeaders = [];
        CapturingAuthService auth = new(
            cookie: "xfsval",
            additional: new Dictionary<string, string>(StringComparer.Ordinal) { ["cf_clearance"] = "CFVAL" });
        CloudflareXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, headers) => { seenHeaders.Add(headers); return Task.FromResult(MyAccountWithApiKeyHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);

        // Spec: requested the cf_clearance cookie and pinned the browser UA to ours.
        Assert.NotNull(auth.LastSpec);
        Assert.Contains("cf_clearance", auth.LastSpec!.Value.AdditionalCookieNames ?? Array.Empty<string>());
        Assert.Equal("UA-TEST/1.0", auth.LastSpec.Value.UserAgentOverride);

        // The my_account GET forwarded BOTH the session and the clearance in one Cookie header.
        Assert.Contains(seenHeaders, h => h is not null
            && h.TryGetValue("Cookie", out string? c)
            && c.Contains("xfss=xfsval", StringComparison.Ordinal)
            && c.Contains("cf_clearance=CFVAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassicMode_SignIn_ForwardsOnlyXfss_AndLeavesSpecDefaults()
    {
        // Regression guard for the 5 classic XFS hosters: even if the jar happened to hold a
        // cf_clearance, classic mode must NOT request it, must NOT pin a UA, and must send ONLY xfss.
        List<IReadOnlyDictionary<string, string>?> seenHeaders = [];
        CapturingAuthService auth = new(
            cookie: "xfsval",
            additional: new Dictionary<string, string>(StringComparer.Ordinal) { ["cf_clearance"] = "CFVAL" });
        TestXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, headers) => { seenHeaders.Add(headers); return Task.FromResult(MyAccountWithApiKeyHtml); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        _ = await pipeline.CheckAccountAsync("u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.NotNull(auth.LastSpec);
        Assert.Null(auth.LastSpec!.Value.AdditionalCookieNames);
        Assert.Null(auth.LastSpec.Value.UserAgentOverride);

        IReadOnlyDictionary<string, string>? myAccountHeaders = seenHeaders.Find(h =>
            h is not null && h.TryGetValue("Cookie", out string? c) && c.Contains("xfss=", StringComparison.Ordinal));
        Assert.NotNull(myAccountHeaders);
        Assert.Equal("xfss=xfsval", myAccountHeaders!["Cookie"]);
    }

    [Fact]
    public async Task GetUploadServer_StorageSubdomainHttps_WhenDowngradeOptedIn_DowngradedToHttp()
    {
        // FlashBit-shape: the API returns https://fsN.host/… for a storage subdomain whose
        // :443 cert is junk (self-signed for an unrelated CN) but serves HTTP cleanly. A hoster
        // that overrides DowngradeUploadServerToHttp=true downgrades so the upload completes.
        Queue<string> getResponses = new(new[]
        {
            """{"msg":"OK","status":200,"sess_id":"sess_x","result":"https://fs1.test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];

        DowngradingXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "DowngradingXfsHost", ApiKey = "k" };
        await foreach (UploadEvent _ in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        { }

        UploadCall call = Assert.Single(calls);
        // Opt-in + host differs from API host test-xfs.example → downgraded.
        Assert.Equal("http://fs1.test-xfs.example/cgi-bin/upload.cgi", call.Endpoint);
    }

    [Fact]
    public async Task GetUploadServer_StorageSubdomainHttps_DefaultRespectsHttps()
    {
        // Hexload-shape: the API returns https://<rand>.host/… for a storage subdomain that
        // REQUIRES https (valid Let's Encrypt cert; over http nginx 301s mid-body → reset). The
        // base default must NOT downgrade — POST verbatim over https, like the anonymous path.
        Queue<string> getResponses = new(new[]
        {
            """{"msg":"OK","status":200,"sess_id":"sess_x","result":"https://fs1.test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];

        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "k" };
        await foreach (UploadEvent _ in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        { }

        // Host differs from the API host, but the default respects the API's scheme → stays https.
        Assert.Equal("https://fs1.test-xfs.example/cgi-bin/upload.cgi", Assert.Single(calls).Endpoint);
    }

    [Fact]
    public async Task GetUploadServer_UploadOnApiHost_KeepsHttps()
    {
        // A defensive check on the other branch: if the API ever returns an upload URL
        // pointing at the API host itself, we leave the scheme alone — the apex has
        // proven-good TLS (we just reached /api/upload/server over it). Only storage
        // subdomains get the downgrade.
        Queue<string> getResponses = new(new[]
        {
            """{"msg":"OK","status":200,"sess_id":"sess_x","result":"https://test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        List<UploadCall> calls = [];

        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "k" };
        await foreach (UploadEvent _ in pipeline.RunAsync(MakeContext(credentials), CancellationToken.None))
        { }

        Assert.Equal("https://test-xfs.example/cgi-bin/upload.cgi", Assert.Single(calls).Endpoint);
    }

    [Fact]
    public async Task SubclassWithJustNameAndHost_CompletesFullApiKeyDirectUploadFlow()
    {
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        List<UploadCall> uploadCalls = [];

        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                uploadCalls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "demo_key" };
        AttemptContext ctx = MakeContext(credentials);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        // Public URL is prefixed with the subclass's Host, not ex-load — proves Host is
        // properly propagated through the shared PublicUrlPrefix derivation.
        Assert.Equal("https://test-xfs.example/demoCode", tc.FileUrl);

        UploadCall call = Assert.Single(uploadCalls);
        Assert.Equal("http://fs1.test-xfs.example/cgi-bin/upload.cgi", call.Endpoint);
        // Origin header should also come from the subclass's Host.
        Assert.Equal("https://test-xfs.example", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task UPBootstrap_KatFileSpanVariantOfApiUrl_ExtractsKeyFromTextContent()
    {
        // KatFile renders the API URL inside a span's text content, not as a value
        // attribute on an input (as Ex-Load does). This fixture is the exact shape
        // observed on the live my_account page on 2026-05-26 — preserve it verbatim so
        // any future regex tightening can't accidentally regress this case.
        const string KatFileMyAccountHtml = """
            <Form method="POST">
            <input type="hidden" name="op" value="my_account">
            <input type="hidden" name="token" value="cb3dfe945b0d5168843daa1282800fef">
            <Table>
              <tr>
                <td>API URL</td>
                <td>
                  <span name="api-url">https://test-xfs.example/api/account/info?key=katfileSpanKey99</span>
                  <br>
                  <a href="?op=my_account&generate_api_key=1&token=cb3dfe945b0d5168843daa1282800fef" name="regen-api-key">change key</a>
                </td>
              </tr>
            </Table>
            </Form>
            """;

        Queue<string> getResponses = new(new[]
        {
            KatFileMyAccountHtml,                                                              // my_account → key already present
            """{"msg":"OK","status":200,"sess_id":"sess_kf","result":"http://fs1.test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"kfCode","file_status":"OK"}]""", Array.Empty<string>()),
        });

        // U/P-bootstrap path: no ApiKey on the DTO, a fake auth service supplies a cookie.
        FakeAuthService auth = new("xfss_katfile_like");
        TestXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
                Task.FromResult(uploads.Dequeue()));

        FileHosterLoginDto credentials = new()
        {
            Id = 99,
            FileHosterName = "TestXfsHost",
            Username = "u@example.com",
            Password = "p",
        };
        AttemptContext ctx = MakeContext(credentials);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<TransferCompleted>());
        // The span-embedded key landed on the DTO without needing a regenerate call.
        Assert.Equal("katfileSpanKey99", credentials.ApiKey);
    }

    [Fact]
    public async Task UPBootstrap_HexloadNamelessValueVariantOfApiUrl_ExtractsKeyFromValueAttribute()
    {
        // Hexload renders the API URL as a bare input value with NO name="api-url" attribute
        // (unlike Ex-Load/KatFile). Captured from the live my_account page 2026-06-13 — the
        // key only appears after a generate, in an input the original three regex branches
        // (all anchored on name="api-url") couldn't match.
        const string HexloadMyAccountHtml = """
            <form method="POST">
            <input type="hidden" name="op" value="my_account">
            <input type="hidden" name="token" value="f7e391c89a1dbcc1fe8fbe53432a7ccd">
            <div class="form-group"><label><strong>API KEY</strong></label>
              <input type="text" size="60" readonly class="form-control-plaintext"
                     value="https://test-xfs.example/api/account/info?key=hexloadNamelessKey42">
            </div>
            </form>
            """;

        Queue<string> getResponses = new(new[]
        {
            HexloadMyAccountHtml,                                                              // my_account → key already present
            """{"msg":"OK","status":200,"sess_id":"sess_hx","result":"http://fs1.test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"hxCode","file_status":"OK"}]""", Array.Empty<string>()),
        });

        FakeAuthService auth = new("xfss_hexload_like");
        TestXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) => Task.FromResult(uploads.Dequeue()));

        FileHosterLoginDto credentials = new()
        {
            Id = 77,
            FileHosterName = "TestXfsHost",
            Username = "u@example.com",
            Password = "p",
        };
        AttemptContext ctx = MakeContext(credentials);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<TransferCompleted>());
        // The bare-value key landed on the DTO via the new name-less regex branch.
        Assert.Equal("hexloadNamelessKey42", credentials.ApiKey);
    }

    [Fact]
    public async Task UPBootstrap_HxfileBareTokenApiKey_ExtractsKeyFromRegenLinkAnchor()
    {
        // Hxfile renders the API key as a BARE token in the my_account "API Key" cell, right
        // before the regenerate link (name="regen-api-key") — not as a /api/account/info?key=…
        // URL like the other XFS hosters. Captured from the live authenticated my_account page
        // 2026-06-24. The fixture keeps a name="token" input so that, if extraction regressed to
        // null, the pipeline would try to generate a key (a third GET) and blow the 2-item queue.
        const string HxfileMyAccountHtml = """
            <form method="POST">
            <input type="hidden" name="op" value="my_account">
            <input type="hidden" name="token" value="dbce9a3a102d33cd0fe8a03f83e33eea">
            <table class="table table-account"><tbody>
            <tr>
                <td>API Key</td>
                <td>
                    7978n5x2t9eqvjjs4deb <a href="?op=my_account&generate_api_key=1&token=dbce9a3a102d33cd0fe8a03f83e33eea" onclick="return confirm('Regenerate api key?')" name="regen-api-key">(Change key)</a> <a href="https://hxfileco.docs.apiary.io/" target=_blank>(API Docs)</a><br/>
                </td>
            </tr>
            </tbody></table>
            </form>
            """;

        Queue<string> getResponses = new(new[]
        {
            HxfileMyAccountHtml,                                                                // my_account → bare-token key already present
            """{"msg":"OK","status":200,"sess_id":"sess_hx","result":"http://fs1.test-xfs.example/cgi-bin/upload.cgi"}""",
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"hxCode","file_status":"OK"}]""", Array.Empty<string>()),
        });

        FakeAuthService auth = new("xfss_hxfile_like");
        TestXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(uploads.Dequeue()));

        FileHosterLoginDto credentials = new()
        {
            Id = 88,
            FileHosterName = "TestXfsHost",
            Username = "hubga54524@minitts.net",
            Password = "p",
        };
        AttemptContext ctx = MakeContext(credentials);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<TransferCompleted>());
        // The bare token landed on the DTO via the new regen-link-anchored branch — and, because
        // only two GETs were queued, WITHOUT a wasteful generate_api_key regenerate round-trip.
        Assert.Equal("7978n5x2t9eqvjjs4deb", credentials.ApiKey);
    }

    [Fact]
    public async Task SubclassMaxFileSizeMessage_UsesSubclassNameNotExLoad()
    {
        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(string.Empty),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "k" };
        AttemptContext ctx = MakeContext(credentials) with { FileSize = 2L * 1024 * 1024 * 1024 };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("TestXfsHost", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Ex-Load", fail.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccount_MyAccountWithoutKeyOrCsrf_KeepsMessageShortAndPutsFullResponseInDetail()
    {
        // Hxfile-shape failure: the captcha sign-in captures a cookie, but my_account comes back
        // as the login page — no api-url input, no CSRF token. The compact "Error: …" line in the
        // Add Account window can't fit a raw HTML page, so Message must stay a short human summary
        // while the COMPLETE response body goes to Detail (the "Details" dialog shows it untruncated).
        const string LoginTailMarker = "HXFILE_LOGIN_PAGE_TAIL_MARKER_7QZ";
        const string LoginPageHtml = """
            <!DOCTYPE html><html lang="en"><head>
            <meta name="a.validate.02" content="2vj1EWr-YigvQvwbtaKFiktev4MPswP0US9m" />
            <meta charset="utf-8"><meta http-equiv="X-UA-Compatible" content="IE=edge">
            <title>Sign in</title></head><body>
            <form action="/login.html" method="post">
              <input name="login"><input name="password" type="password">
            </form>
            <div id="tail">HXFILE_LOGIN_PAGE_TAIL_MARKER_7QZ</div>
            </body></html>
            """;

        // Fixture has neither name="api-url" nor name="token", so both extractors return null and
        // the verify path lands on the "no key OR CSRF" branch — and it's well over the 200-char
        // Snippet() cap, with the marker at the tail, so the marker proves Detail isn't truncated.
        FakeAuthService auth = new("xfsts_cookie_like");
        TestXfsHostPipeline pipeline = new(
            authService: auth,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(LoginPageHtml),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);

        // Message: the short, HTML-free summary.
        Assert.Contains("did not contain an API key OR a CSRF token", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LoginTailMarker, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", result.Message, StringComparison.OrdinalIgnoreCase);

        // Detail: the summary plus the full, untruncated response body. The tail marker sits well
        // past the old 200-char Snippet cut, so its presence proves nothing was truncated.
        Assert.NotNull(result.Detail);
        Assert.Contains("did not contain an API key OR a CSRF token", result.Detail!, StringComparison.Ordinal);
        Assert.Contains(LoginTailMarker, result.Detail!, StringComparison.Ordinal);
    }

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "TestXfsHost",
        Credentials = credentials,
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

    /// <summary>Minimal <see cref="IInteractiveAuthService"/> for the U/P-bootstrap test.</summary>
    private sealed class FakeAuthService(string? cannedCookie) : IInteractiveAuthService
    {
        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            InteractiveAuthResult? result = cannedCookie is null
                ? null
                : new InteractiveAuthResult(cannedCookie, CapturedUsername: null);
            return Task.FromResult(result);
        }
    }

    /// <summary>Auth service that records the spec it was handed and returns a result with optional
    /// additional cookies — used to assert cf_clearance capture + UA pinning + cookie forwarding.</summary>
    private sealed class CapturingAuthService(string cookie, IReadOnlyDictionary<string, string>? additional) : IInteractiveAuthService
    {
        public InteractiveAuthSpec? LastSpec { get; private set; }

        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            return Task.FromResult<InteractiveAuthResult?>(
                new InteractiveAuthResult(cookie, CapturedUsername: null, AdditionalCookies: additional));
        }
    }
}
