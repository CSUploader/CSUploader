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
/// Pipeline tests for the API-centric <see cref="ExLoadPipeline"/>. Two credential paths
/// converge on the same API-key upload flow:
/// <list type="bullet">
///   <item><b>API-key direct</b>: <see cref="FileHosterLoginDto.ApiKey"/> already set →
///   no WebView, no cookie, just the two API calls.</item>
///   <item><b>U/P bootstrap</b>: cookie captured via the fake auth service, my_account
///   scrape extracts (or generates) the API key, persists onto the DTO.</item>
/// </list>
/// Upload itself is exercised through a captured-call <c>uploadOverride</c> so we can
/// assert request shape without touching the network.
/// </summary>
public class ExLoadPipelineTests
{
    /// <summary>Realistic minimal my_account page with an existing api-url input.</summary>
    private const string MyAccountWithApiKeyHtml = """
        <!doctype html><html><body>
        <form method="POST">
          <input type="hidden" name="op" value="my_account">
          <input type="hidden" name="token" value="csrftokenabc123">
          <input type="text" readonly name="api-url" value="https://ex-load.com/api/account/info?key=key_existing_one">
        </form>
        </body></html>
        """;

    /// <summary>my_account page WITHOUT an api-url input — used to drive the generate path.</summary>
    private const string MyAccountWithoutApiKeyHtml = """
        <!doctype html><html><body>
        <form method="POST">
          <input type="hidden" name="op" value="my_account">
          <input type="hidden" name="token" value="csrftokenneedsgenerate">
        </form>
        </body></html>
        """;

    private const string UploadServerOkJson = """{"msg":"OK","server_time":"2026-05-25 16:14:57","status":200,"sess_id":"sess_abc","result":"http://fs40.ex-load.com/cgi-bin/upload.cgi"}""";

    private const string AccountInfoOkJson = """{"msg":"OK","server_time":"2026-05-25 16:13:59","status":200,"result":{"email":"u@example.com","premium_expire":"2027-12-31 00:00:00","balance":"0.00000"}}""";

    private const string AccountInfoExpiredJson = """{"msg":"OK","server_time":"2026-05-25 16:13:59","status":200,"result":{"email":"u@example.com","premium_expire":"2024-01-01 00:00:00","balance":"0.00000"}}""";

    private const string UploadOkJson = """[{"file_code":"xyz789","file_status":"OK"}]""";

    [Fact]
    public async Task RunAsync_WithPersistedApiKey_SkipsBootstrapAndUploadsViaApi()
    {
        Queue<(string Url, IReadOnlyDictionary<string, string>? Headers)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new()
        {
            Id = 1,
            FileHosterName = "ExLoad",
            Username = string.Empty,
            ApiKey = "key_pasted_by_user",
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://ex-load.com/xyz789", tc.FileUrl);
        Assert.Equal(0, auth.CallCount); // No WebView!
        Assert.DoesNotContain(events, e => e is AuthStarted);

        // Single API GET to /api/upload/server with the API key as a query param.
        (string Url, IReadOnlyDictionary<string, string>? Headers) serverCall = Assert.Single(getCalls);
        Assert.StartsWith("https://ex-load.com/api/upload/server?key=key_pasted_by_user", serverCall.Url, StringComparison.Ordinal);

        // Upload landed on the per-user subdomain returned by the API, with sess_id passed through.
        UploadCall up = Assert.Single(uploadCalls);
        Assert.Equal("http://fs40.ex-load.com/cgi-bin/upload.cgi", up.Endpoint);
        Assert.Equal("sess_abc", up.ExtraFields["sess_id"]);
    }

    [Fact]
    public async Task RunAsync_UploadCall_IncludesOriginAndSecFetchHeadersForBrowserParity()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "ExLoad", ApiKey = "key_x" };

        await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        UploadCall call = Assert.Single(uploadCalls);
        Assert.NotNull(call.Headers);
        Assert.Equal("https://ex-load.com", call.Headers!["Origin"]);
        Assert.Equal("same-site", call.Headers["Sec-Fetch-Site"]);
        Assert.Equal("cors", call.Headers["Sec-Fetch-Mode"]);
        Assert.Equal("empty", call.Headers["Sec-Fetch-Dest"]);
    }

    [Fact]
    public async Task RunAsync_UploadServer403_TreatedAsAuthExpiredAndClearsApiKey()
    {
        const string Forbidden = """{"msg":"forbidden","status":403,"sess_id":"","result":""}""";
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { Forbidden });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "ExLoad", ApiKey = "dead_key" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Null(credentials.ApiKey); // cleared so next attempt re-bootstraps
    }

    [Fact]
    public async Task RunAsync_NoApiKeyAndNoUsername_FailsWithoutPoppingWebView()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "ExLoad", Username = string.Empty, ApiKey = null };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Equal(0, auth.CallCount);
    }

    [Fact]
    public async Task RunAsync_UPBootstrap_PopsWebViewScrapesMyAccountAndPersistsApiKey()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[]
        {
            MyAccountWithApiKeyHtml, // 1. my_account scrape returns the existing key
            UploadServerOkJson,      // 2. /api/upload/server with the freshly-derived key
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        FakeAuthService auth = new("xfss_from_webview");
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 1,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            Password = "p",
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(1, auth.CallCount);
        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);

        // The derived API key landed on the credentials AND the cookie/pin got cleared
        // (the API key is IP-agnostic so we don't need them anymore).
        Assert.Equal("key_existing_one", credentials.ApiKey);
        Assert.Null(credentials.SessionCookie);
        Assert.Null(credentials.SessionCookieExpiresUtc);
        Assert.Null(credentials.PinnedProxyId);

        // First GET: my_account (with cookie header). Second GET: /api/upload/server (no cookie needed).
        Assert.Equal(2, getCalls.Count);
        (string firstUrl, IReadOnlyDictionary<string, string>? firstHeaders) = getCalls.Dequeue();
        Assert.Contains("op=my_account", firstUrl, StringComparison.Ordinal);
        Assert.NotNull(firstHeaders);
        Assert.Contains("xfss=xfss_from_webview", firstHeaders!["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UPBootstrap_MissingKeyTriggersGenerateThenRescrape()
    {
        Queue<(string Url, IReadOnlyDictionary<string, string>? Headers)> getCalls = new();
        Queue<string> getResponses = new(new[]
        {
            MyAccountWithoutApiKeyHtml,   // 1. initial scrape — no api-url input
            "ignored",                    // 2. generate request — body unused
            MyAccountWithApiKeyHtml,      // 3. re-scrape after generate — now has key
            UploadServerOkJson,           // 4. /api/upload/server
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        FakeAuthService auth = new("xfss_fresh");
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 1,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            Password = "p",
        };

        await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Equal("key_existing_one", credentials.ApiKey);

        // Verify the generate URL was actually hit with the CSRF token from the page.
        List<(string Url, IReadOnlyDictionary<string, string>? Headers)> calls = [.. getCalls];
        Assert.Equal(4, calls.Count);
        Assert.Contains("generate_api_key=1", calls[1].Url, StringComparison.Ordinal);
        Assert.Contains("token=csrftokenneedsgenerate", calls[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UPBootstrap_UserCancelsWebView_FailsWithoutPersisting()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null); // user cancels
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new()
        {
            Id = 1,
            FileHosterName = "ExLoad",
            Username = "u@example.com",
            Password = "p",
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Null(credentials.ApiKey);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsUnauthorized_ClearsApiKey()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"","file_status":"Unauthorized"}]""", Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "ExLoad", ApiKey = "expired_key" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Null(credentials.ApiKey);
    }

    [Fact]
    public async Task CheckAccountAsync_WithApiKey_HitsAccountInfoAndClassifiesPremium()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoOkJson });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty,
            password: string.Empty,
            apiKey: "key_premium",
            handler: handler,
            proxy: ProxyChoice.Direct,
            ct: CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
        Assert.Equal("key_premium", result.ApiKey); // echoed back so SettingsVM can persist it
        Assert.NotNull(result.PremiumExpiry);
    }

    [Fact]
    public async Task CheckAccountAsync_WithApiKey_PremiumExpiredClassifiesAsFree()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoExpiredJson });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            string.Empty, string.Empty, "key_with_expired_premium", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
    }

    [Fact]
    public async Task CheckAccountAsync_NoApiKey_PopsWebViewAndDerivesKeyFromMyAccount()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[]
        {
            MyAccountWithApiKeyHtml,
            AccountInfoOkJson,
        });
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new("xfss_from_webview");
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("key_existing_one", result.ApiKey); // returned so SettingsVM persists
        Assert.Equal(1, auth.CallCount);
    }

    [Fact]
    public async Task CheckAccountAsync_NoApiKey_UserCancelsWebView_ReturnsInvalid()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.ApiKey);
    }

    [Fact]
    public async Task RunAsync_FileExceedsMaxFileSize_FailsWithoutTouchingApiOrWebView()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "ExLoad", ApiKey = "k" };
        AttemptContext ctx = MakeContext(credentials) with { FileSize = 2L * 1024 * 1024 * 1024 };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Ex-Load", fail.Reason, StringComparison.Ordinal);
        Assert.Equal(0, auth.CallCount);
        Assert.Empty(getCalls);
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
        FakeAuthService auth,
        Queue<(string Url, IReadOnlyDictionary<string, string>? Headers)> getCalls,
        Queue<string> getResponses,
        Queue<HttpResponseSnapshot> uploads,
        out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new ExLoadPipeline(
            authService: auth,
            loginRepository: null,
            getOverride: (url, headers) =>
            {
                getCalls.Enqueue((url, headers));
                return Task.FromResult(getResponses.Dequeue());
            },
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
        Credentials = credentials ?? new FileHosterLoginDto { Id = 42, FileHosterName = "ExLoad", ApiKey = "default_key" },
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
    /// Fake auth service returning a canned xfss cookie (or null for user-cancel). Counts
    /// invocations so tests can assert the API-key direct path doesn't pop the WebView.
    /// </summary>
    private sealed class FakeAuthService(string? cannedCookie) : IInteractiveAuthService
    {
        public int CallCount { get; private set; }

        public Task<string?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(cannedCookie);
        }
    }
}
