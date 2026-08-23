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

    // Verbatim shape from the live /api/account/info response: storage_used is a byte
    // count, storage_left is "inf" for ex-load's unlimited accounts.
    private const string AccountInfoWithStorageJson = """{"msg":"OK","server_time":"2026-06-10 16:02:34","status":200,"result":{"email":"u@example.com","premium_expire":"2027-12-31 00:00:00","balance":"0.00000","storage_used":"415593052","storage_left":"inf"}}""";

    // Same but with a finite storage_left (hypothetical capped account) to prove quota math.
    private const string AccountInfoCappedStorageJson = """{"msg":"OK","server_time":"2026-06-10 16:02:34","status":200,"result":{"email":"u@example.com","premium_expire":"2027-12-31 00:00:00","balance":"0.00000","storage_used":"400000000","storage_left":"600000000"}}""";

    // Verbatim KatFile shape: storage_left is a JSON NUMBER (not a string), storage_used a
    // string. The old string-typed DTO threw on the number and the whole deserialize failed.
    private const string AccountInfoNumericStorageLeftJson = """{"result":{"balance":"0.00000","storage_left":2198032008075,"premium_expire":"2027-12-31 00:00:00","storage_used":"991247477","email":"u@example.com"},"status":200,"server_time":"2026-06-10 18:19:12","msg":"OK"}""";

    // Verbatim Hexload shape (live 2026-06-13): an EMPTY account reports storage_used as JSON
    // null (its dashboard shows 0.00 GB) and storage_left "inf". Distinct from the field being
    // absent — must surface used=0, not blank.
    private const string AccountInfoNullStorageUsedJson = """{"msg":"OK","server_time":"2026-06-13 12:12:34","status":200,"result":{"email":"u@example.com","premium_expire":"2027-12-31 00:00:00","balance":"0.00000","storage_used":null,"storage_left":"inf"}}""";

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
            FileHosterName = "Ex-Load",
            Username = string.Empty,
            ApiKey = "key_pasted_by_user",
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://ex-load.com/xyz789", tc.FileUrl);
        Assert.Equal(0, auth.CallCount); // No WebView!
        Assert.DoesNotContain(events, e => e is AuthStarted);

        // Single API GET to /api/upload/server with the API key as a query param.
        (string Url, IReadOnlyDictionary<string, string>? Headers) = Assert.Single(getCalls);
        Assert.StartsWith("https://ex-load.com/api/upload/server?key=key_pasted_by_user", Url, StringComparison.Ordinal);

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

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "key_x" };

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

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "dead_key" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Null(credentials.ApiKey); // cleared so next attempt re-bootstraps
    }

    // === Transient node failures on the API-key path ===
    // These mirror the web-form path's retry (see UploadyPipelineTests). Nothing about a broken node
    // is specific to how the caller authenticated, but the API path went without the retry until
    // 2026-08-01 — it was simply where the fault wasn't first diagnosed. Every API-key XFS hoster
    // (KatFile, Hexload, Hxfile, Send.now, Ufile, …) inherits this from the shared base.

    private const string NodeFailureJson =
        """[{"file_code":"undef","file_status":"failed while requesting fs.cgi: <html><title>500 Internal Server Error</title></html>"}]""";

    private const string UploadServerSecondNodeJson =
        """{"msg":"OK","server_time":"2026-05-25 16:15:02","status":200,"sess_id":"sess_two","result":"http://fs77.ex-load.com/cgi-bin/upload.cgi"}""";

    [Fact]
    public async Task RunAsync_ApiPath_NodeBreaksAfterTakingTheBytes_RetriesOnceAgainstAFreshServer()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        // The API hands out the node, so a second /api/upload/server call is what moves the retry off
        // the broken one — the retry must not simply re-send to the server that just failed.
        Queue<string> getResponses = new(new[] { UploadServerOkJson, UploadServerSecondNodeJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, NodeFailureJson, Array.Empty<string>()),
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "key_x" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Equal("https://ex-load.com/xyz789", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        Assert.Equal(2, uploadCalls.Count);
        Assert.Equal("http://fs40.ex-load.com/cgi-bin/upload.cgi", uploadCalls[0].Endpoint);
        Assert.Equal("http://fs77.ex-load.com/cgi-bin/upload.cgi", uploadCalls[1].Endpoint); // a DIFFERENT node
        Assert.Equal("sess_two", uploadCalls[1].ExtraFields["sess_id"]);                     // and its own sess_id

        // One transfer as far as the UI is concerned — the retry is ours, not the user's.
        Assert.Single(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_ApiPath_NodeFailurePersists_FailsAfterExactlyOneRetry()
    {
        // The retry re-sends the whole file, so it must never become a loop.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson, UploadServerSecondNodeJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, NodeFailureJson, Array.Empty<string>()),
            new HttpResponseSnapshot(200, NodeFailureJson, Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "key_x" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains("fs.cgi", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, uploadCalls.Count); // the original send + exactly one retry
    }

    [Fact]
    public async Task RunAsync_ApiPath_FileRejected_IsNeverReUploaded()
    {
        // The counterpart guard: a verdict on the FILE must not be retried, or an oversized file is
        // sent twice to be refused twice.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """[{"file_code":"undef","file_status":"File is too big"}]""", Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "key_x" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains("too big", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(uploadCalls);   // sent once…
        Assert.Single(getCalls);      // …and no second server was even asked for
    }

    [Fact]
    public async Task RunAsync_ApiPath_RetryCannotResolveAServer_ReportsTheNodesOwnFailure()
    {
        // The re-resolve failing is a symptom; the node's refusal is the diagnosis. Reporting
        // "couldn't resolve upload server" would send the user looking in the wrong place.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { UploadServerOkJson, """{"msg":"maintenance","status":500,"sess_id":"","result":""}""" });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, NodeFailureJson, Array.Empty<string>()),
        });
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out List<UploadCall> uploadCalls);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "key_x" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(credentials), CancellationToken.None));

        Assert.Contains("fs.cgi", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(uploadCalls); // nothing re-sent, because there was nowhere to send it
    }

    [Fact]
    public async Task RunAsync_NoApiKeyAndNoUsername_FailsWithoutPoppingWebView()
    {
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new();
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", Username = string.Empty, ApiKey = null };

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
            FileHosterName = "Ex-Load",
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
            FileHosterName = "Ex-Load",
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
            FileHosterName = "Ex-Load",
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

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "Ex-Load", ApiKey = "expired_key" };

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

    // RunAsync_FileExceedsMaxFileSize_FailsWithoutTouchingApiOrWebView removed —
    // ExLoadPipeline.MaxFileSize is now null (member uploads have no cap; the 100 MB
    // limit is guest-only). The base XFS pre-check is still exercised via the other
    // XFS-family pipelines that DO declare a cap (KatFile, TakeFile, etc.).

    [Fact]
    public async Task CheckAccountAsync_ApiKeyDirect_SurfacesStorageUsedFromAccountInfoJson()
    {
        // Storage comes straight from the /api/account/info JSON (storage_used +
        // storage_left) — no cookie / my_files HTML scrape. storage_left="inf" → quota
        // null → grid's Available cell renders blank.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoWithStorageJson });
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new(null);
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: "key_existing_one", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(415_593_052L, result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes); // "inf" → unlimited → Available column blank
        Assert.Equal(0, auth.CallCount); // api-key path never pops the WebView
        Assert.Empty(getResponses);
    }

    [Fact]
    public async Task CheckAccountAsync_ApiKeyDirect_FiniteStorageLeft_ComputesQuota()
    {
        // When storage_left is a real number (not "inf"), quota = used + left so the
        // grid's Available cell shows the remaining space.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoCappedStorageJson });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: "key_existing_one", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(400_000_000L, result.StorageUsedBytes);
        Assert.Equal(1_000_000_000L, result.StorageQuotaBytes); // used + left
    }

    [Fact]
    public async Task CheckAccountAsync_ApiKeyDirect_NumericStorageLeft_ParsesAndComputesQuota()
    {
        // Regression: KatFile returns storage_left as a JSON NUMBER (not a string). The old
        // string-typed DTO threw on the number and the whole /api/account/info deserialize
        // failed, surfacing the misleading "API key was rejected" error. The JsonElement
        // fields now tolerate both string and number shapes.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoNumericStorageLeftJson });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: "key_existing_one", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(991_247_477L, result.StorageUsedBytes);
        // quota = used + left = 991247477 + 2198032008075
        Assert.Equal(991_247_477L + 2_198_032_008_075L, result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_ApiKeyDirect_NoStorageFields_LeavesStorageNull()
    {
        // Older/other XFS hosters whose /api/account/info omits storage_used/left. Must
        // not throw — storage stays null and the grid renders blank Used/Available cells.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoOkJson }); // no storage_* fields
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: "key_existing_one", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_ApiKeyDirect_NullStorageUsed_TreatedAsZeroUsed()
    {
        // Hexload returns storage_used:null for an empty account (vs Ex-Load/KatFile's real
        // number). Surface used=0 (its own dashboard shows 0.00 GB), NOT blank, while "inf"
        // storage_left keeps quota null (Unlimited). Contrast with the absent-field test above,
        // which stays null — proving present-but-null is distinguishable from absent.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[] { AccountInfoNullStorageUsedJson });
        Queue<HttpResponseSnapshot> uploads = new();
        ExLoadPipeline pipeline = MakePipeline(new FakeAuthService(null), getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: "key_existing_one", handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_NoApiKey_SurfacesStorageFromDerivedAccountInfo()
    {
        // U/P→WebView path: after deriving the key, the base parses storage from the same
        // /api/account/info JSON it already fetched — no extra round-trip.
        Queue<(string, IReadOnlyDictionary<string, string>?)> getCalls = new();
        Queue<string> getResponses = new(new[]
        {
            MyAccountWithApiKeyHtml,    // 1. my_account scrape (returns existing api-url)
            AccountInfoWithStorageJson, // 2. /api/account/info (premium / email / storage)
        });
        Queue<HttpResponseSnapshot> uploads = new();
        FakeAuthService auth = new("xfss_from_webview");
        ExLoadPipeline pipeline = MakePipeline(auth, getCalls, getResponses, uploads, out _);
        using HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u@example.com", "p", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(415_593_052L, result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
        Assert.Empty(getResponses); // both responses consumed — no my_files round-trip
    }

    [Fact]
    public void Properties_DeclareExLoadFreeTierLimits()
    {
        // Ex-Load's 100 MB per-file cap is for anonymous guests only — our pipeline
        // always uploads as a logged-in member, which has no documented cap. Override
        // is in ExLoadPipeline.cs; pinned here so a future XFS-base default change
        // doesn't accidentally reintroduce a cap that would block large legitimate
        // uploads at queue time.
        ExLoadPipeline pipeline = new();
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.Equal("Ex-Load", pipeline.Name);
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
        HosterName = "Ex-Load",
        Credentials = credentials ?? new FileHosterLoginDto { Id = 42, FileHosterName = "Ex-Load", ApiKey = "default_key" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
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

        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            CallCount++;
            InteractiveAuthResult? result = cannedCookie is null
                ? null
                : new InteractiveAuthResult(cannedCookie, CapturedUsername: null);
            return Task.FromResult(result);
        }
    }
}
