// <copyright file="ExLoadPipelineTests.cs" company="CSUploader">
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
/// Pipeline tests for <see cref="ExLoadPipeline"/>. Ex-Load's login is captcha-gated, so
/// the only fixture difference from <see cref="BRuploadPipelineUploadTests"/> is that the
/// "login" round-trip is replaced by a fake <see cref="IInteractiveAuthService"/> that
/// returns a canned cookie. Upload-shape assertions (Origin, Sec-Fetch-*, scraped action
/// URL, sess_id selection) are mirrored from the BRupload tests because the upload-CGI
/// backend is the same XFileSharing template.
/// </summary>
public class ExLoadPipelineTests
{
    // The upload form HTML deliberately points at a per-user upload subdomain to confirm
    // the pipeline uses the scraped URL rather than the main host. Same shape as BRupload.
    private const string UploadFormHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" method="POST" enctype="multipart/form-data" action="https://s5.ex-load.com/cgi-bin/upload.cgi?upload_type=file&utype=reg">
          <input type="hidden" name="sess_id" value="formSessExload">
          <input type="hidden" name="utype" value="reg">
          <input type="file" name="file_0">
          <input type="submit" name="upload" value="Start upload">
        </form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_FirstUse_RequestsCookieFromAuthServiceAndUploadsThroughScrapedUrl()
    {
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"abc123","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("cookieFromWebView");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 7, FileHosterName = "ExLoad", Username = "u@example.com" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://ex-load.com/abc123", tc.FileUrl);
        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);

        // The WebView dialog should have been opened exactly once.
        Assert.Equal(1, auth.CallCount);

        // The session cookie + an expiry timestamp should now be written back to the
        // credentials DTO so the caller can persist them.
        Assert.Equal("cookieFromWebView", credentials.SessionCookie);
        Assert.True(credentials.SessionCookieExpiresUtc > DateTime.UtcNow.AddDays(6));

        // PinnedProxyId is set from the runner-supplied proxy so AttemptRunner can route
        // subsequent uploads through the same IP the cookie was issued from.
        // MakeContext() defaults to ProxyChoice.Direct (Id=0), so the pin is 0 here.
        Assert.Equal(0, credentials.PinnedProxyId);

        // Upload must POST to the scraped subdomain (not the main ex-load.com host) and
        // must use the sess_id from the form, not the cookie value.
        UploadCall call = Assert.Single(uploadCalls);
        Assert.Equal("https://s5.ex-load.com/cgi-bin/upload.cgi?upload_type=file&utype=reg", call.Endpoint);
        Assert.Equal("formSessExload", call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_FirstUseWithProxy_PinsProxyIdAndPassesProxyToAuthService()
    {
        // The cornerstone of XFileSharing IP-binding mitigation: the proxy the runner
        // picked must (a) be passed to the WebView so the cookie is issued from that IP,
        // and (b) be pinned onto the credentials so subsequent uploads reuse it.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("cookie");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new() { Id = 13, FileHosterName = "ExLoad", Username = "u@example.com" };
        ProxyChoice pickedProxy = new(99, new System.Net.WebProxy("http://proxy.example:8080"), "http://proxy.example:8080");
        AttemptContext ctx = MakeContext(credentials) with { Proxy = pickedProxy };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Same(pickedProxy, auth.LastProxy);
        Assert.Equal(99, credentials.PinnedProxyId);
    }

    [Fact]
    public async Task RunAsync_ProxyDiffersFromPin_InvalidatesPersistedCookieAndReSignsInThroughNewProxy()
    {
        // Self-healing flow: the original pinned proxy was disabled, AttemptRunner rotated
        // off-pin and handed us a different proxy. Pipeline must detect the mismatch,
        // throw away the (now IP-mismatched) cookie, and pop the WebView again through
        // the new proxy. The new pin must point at the new proxy.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("freshCookieAfterRecovery");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 15,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            SessionCookie = "deadCookieBoundToOldProxy",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1), // not time-expired
            PinnedProxyId = 1, // old pin
        };
        ProxyChoice rotatedProxy = new(2, new System.Net.WebProxy("http://newproxy:8080"), "http://newproxy:8080");
        AttemptContext ctx = MakeContext(credentials) with { Proxy = rotatedProxy };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        // The WebView must have been opened despite the cookie being non-expired —
        // mismatch trumps freshness.
        Assert.Equal(1, auth.CallCount);
        Assert.Same(rotatedProxy, auth.LastProxy);

        // New cookie and new pin pointing at the recovery proxy.
        Assert.Equal("freshCookieAfterRecovery", credentials.SessionCookie);
        Assert.Equal(2, credentials.PinnedProxyId);
    }

    [Fact]
    public async Task RunAsync_PersistedCookieReusedWithoutWebView_LeavesPinnedProxyIdUntouched()
    {
        // Sanity check that an upload through an account whose pin matches the runner's
        // proxy doesn't overwrite the pin or pop the WebView. The cached-cookie + matching-
        // pin path is the steady-state for repeated uploads on the same account.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("shouldNeverBeUsed");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 14,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            SessionCookie = "validCookie",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
            PinnedProxyId = 77,
        };
        // Pin (77) matches the runner-supplied proxy id (77) — no mismatch, cookie stays.
        ProxyChoice pinnedProxy = new(77, null, "http://pinned:8080");
        AttemptContext ctx = MakeContext(credentials) with { Proxy = pinnedProxy };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal(0, auth.CallCount);
        Assert.Equal(77, credentials.PinnedProxyId);
    }

    [Fact]
    public async Task RunAsync_UploadCall_IncludesOriginAndSecFetchHeadersForBrowserParity()
    {
        // Same reasoning as BRupload: XFileSharing's upload.cgi routes requests without
        // Origin to the anonymous-upload path, which surfaces as the misleading "uploads
        // are not enabled for your account type" error. Pin the full header set so a
        // refactor can't drop any one of them.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, new FakeAuthService("ck"), out List<UploadCall> uploadCalls);

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        UploadCall call = Assert.Single(uploadCalls);
        Assert.NotNull(call.Headers);
        Assert.Equal("https://ex-load.com", call.Headers!["Origin"]);
        Assert.Equal("same-site", call.Headers["Sec-Fetch-Site"]);
        Assert.Equal("cors", call.Headers["Sec-Fetch-Mode"]);
        Assert.Equal("empty", call.Headers["Sec-Fetch-Dest"]);
        // Cookie deliberately NOT sent on the upload subdomain — same scoping reason as BRupload.
        Assert.False(call.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task RunAsync_PersistedCookieStillValid_SkipsAuthServiceAndGoesStraightToUploadForm()
    {
        // Credentials already carry a valid persisted cookie → the WebView must NOT pop.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("shouldNeverBeUsed");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 11,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            SessionCookie = "persistedCookie",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        // No interactive sign-in should have happened — the persisted cookie was honoured.
        Assert.Equal(0, auth.CallCount);
        Assert.DoesNotContain(events, e => e is AuthStarted);
    }

    [Fact]
    public async Task RunAsync_PersistedCookieExpired_FallsBackToAuthService()
    {
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("freshFromWebView");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 22,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            SessionCookie = "ancient",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-1),
        };

        await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Equal(1, auth.CallCount);
        Assert.Equal("freshFromWebView", credentials.SessionCookie);
    }

    [Fact]
    public async Task RunAsync_AuthServiceReturnsNull_YieldsAuthFailedAndAttemptFailed()
    {
        // User cancelled the WebView. Pipeline must surface both AuthFailed and AttemptFailed
        // without attempting any uploads.
        Queue<string> gets = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out List<UploadCall> uploadCalls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(uploadCalls);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsUnauthorized_DropsCachedAuthAndClearsPersistedCookie()
    {
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"","file_status":"Unauthorized"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("stale");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 33,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            SessionCookie = "stale",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(1),
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        // Persisted cookie should be cleared so the next attempt won't re-load the dead cookie.
        Assert.Null(credentials.SessionCookie);
        Assert.Null(credentials.SessionCookieExpiresUtc);
    }

    [Fact]
    public async Task RunAsync_UploadFormMissingAction_YieldsAuthFailed()
    {
        Queue<string> gets = new(new[] { "<html>no form</html>" });
        ExLoadPipeline pipeline = MakePipeline(gets, uploads: new(), new FakeAuthService("ck"), out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
    }

    [Fact]
    public async Task RunAsync_UploadFormWithoutSessId_FallsBackToCookieValue()
    {
        const string formWithoutSessId = """
            <form id="uploadfile" method="POST" enctype="multipart/form-data" action="https://srv.ex-load.com/cgi-bin/upload.cgi">
              <input type="file" name="file_0">
            </form>
            """;
        Queue<string> gets = new(new[] { formWithoutSessId });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"ok","file_status":"OK"}]""", Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, new FakeAuthService("xfssFallback"), out List<UploadCall> uploadCalls);

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        UploadCall call = Assert.Single(uploadCalls);
        Assert.Equal("xfssFallback", call.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_SecondAttemptReusesCachedSession_SkipsAuthServiceAndUploadForm()
    {
        // First attempt warms the in-memory cache; second attempt must not touch either
        // the auth service or the upload_form endpoint.
        Queue<string> gets = new(new[] { UploadFormHtml });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"first","file_status":"OK"}]""", Array.Empty<string>()),
            new HttpResponseSnapshot(200, """[{"file_code":"second","file_status":"OK"}]""", Array.Empty<string>()),
        });
        FakeAuthService auth = new("ck");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.DoesNotContain(second, e => e is AuthStarted);
        TransferCompleted tc = Assert.Single(second.OfType<TransferCompleted>());
        Assert.Equal("https://ex-load.com/second", tc.FileUrl);
        Assert.Equal(1, auth.CallCount);
        Assert.Empty(gets);
        Assert.Empty(uploads);
    }

    [Fact]
    public async Task RunAsync_FileExceedsMaxFileSize_YieldsAttemptFailedWithoutAnyHttp()
    {
        // Pre-check must short-circuit before either the auth service or the network.
        Queue<string> gets = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new("ck");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads, auth, out _);

        AttemptContext ctx = MakeContextWithSize(2L * 1024 * 1024 * 1024);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Ex-Load", fail.Reason, StringComparison.Ordinal);
        Assert.Equal(0, auth.CallCount);
    }

    [Fact]
    public async Task CheckAccountAsync_HappyPath_ReturnsCapturedCookieAndExpiryOnResult()
    {
        // Settings VM stamps these onto the credentials DTO so the first real upload
        // can reuse the cookie without re-popping the WebView. This pins the round-trip.
        Queue<string> gets = new(new[] { UploadFormHtml });
        FakeAuthService auth = new("cookieFromCheck");
        ExLoadPipeline pipeline = MakePipeline(gets, uploads: new(), auth, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "ignored", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("cookieFromCheck", result.SessionCookie);
        Assert.NotNull(result.SessionCookieExpiresUtc);
        Assert.True(result.SessionCookieExpiresUtc!.Value > DateTime.UtcNow.AddDays(6));
        Assert.Equal(1, auth.CallCount);
    }

    [Fact]
    public async Task CheckAccountAsync_UserCancelled_ReturnsInvalidWithoutCookie()
    {
        Queue<string> gets = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(gets, uploads: new(), auth, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "ignored", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.SessionCookie);
        Assert.Null(result.SessionCookieExpiresUtc);
    }

    [Fact]
    public void Properties_DeclareExLoadFreeTierLimits()
    {
        ExLoadPipeline pipeline = new();
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal(30, pipeline.MaxFilesPerPackage);
        Assert.Equal("ExLoad", pipeline.Name);
    }

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }
        return events;
    }

    private static ExLoadPipeline MakePipeline(
        Queue<string> gets,
        Queue<HttpResponseSnapshot> uploads,
        FakeAuthService auth,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new ExLoadPipeline(
            authService: auth,
            loginRepository: null,
            getOverride: _ => gets.Dequeue(),
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                captured.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(extraFields),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });
    }

    private static AttemptContext MakeContext(FileHosterLoginDto? credentials = null) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "ExLoad",
        Credentials = credentials ?? new FileHosterLoginDto { Id = 42, FileHosterName = "ExLoad", Username = "u@example.com" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static AttemptContext MakeContextWithSize(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\big.iso",
        FileName = "big.iso",
        FileSize = size,
        HosterName = "ExLoad",
        Credentials = new FileHosterLoginDto { Id = 42, FileHosterName = "ExLoad", Username = "u@example.com" },
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

    /// <summary>
    /// Fake <see cref="IInteractiveAuthService"/> returning a canned cookie (or null to
    /// simulate user cancellation). Tracks how many times it was called so tests can
    /// assert the persisted-cookie path skipped it entirely. Also records the proxy each
    /// call received so tests can pin the pipeline's "route through the upload proxy"
    /// behaviour.
    /// </summary>
    private sealed class FakeAuthService(string? cannedCookie) : IInteractiveAuthService
    {
        public int CallCount { get; private set; }

        public ProxyChoice? LastProxy { get; private set; }

        public Task<string?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            CallCount++;
            LastProxy = proxy;
            return Task.FromResult(cannedCookie);
        }
    }
}
