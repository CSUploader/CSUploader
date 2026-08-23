// <copyright file="FileBoomPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Text;
using System.Text.Json;
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

public class FileBoomPipelineTests
{
    [Fact]
    public void Properties_DeclareFileBoomConfigAndFreeTierCap()
    {
        FileBoomPipeline pipeline = new();

        Assert.Equal("FileBoom", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        FileBoomPipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Fact]
    public void IsUserScopedAccessToken_OnUserAudPayload_ReturnsTrue()
    {
        string jwt = MakeJwt(new { sub = "68627552", aud = "user", role = "registered", exp = FutureUnix() });
        Assert.True(MoneyPlatformPipeline.IsUserScopedAccessToken(jwt));
    }

    [Fact]
    public void IsUserScopedAccessToken_OnClientAudPayload_ReturnsFalse()
    {
        // Pre-login bootstrap token — same cookie name (accessToken), wrong audience.
        // This is the case the validator MUST reject so the WebView doesn't close on
        // the first NavigationCompleted.
        string jwt = MakeJwt(new { sub = "5b46fd6bfc4e", aud = "client", exp = FutureUnix() });
        Assert.False(MoneyPlatformPipeline.IsUserScopedAccessToken(jwt));
    }

    [Fact]
    public void IsUserScopedAccessToken_OnMalformedJwt_ReturnsFalseWithoutThrowing()
    {
        Assert.False(MoneyPlatformPipeline.IsUserScopedAccessToken("not.a.jwt"));
        Assert.False(MoneyPlatformPipeline.IsUserScopedAccessToken("only-one-segment"));
        Assert.False(MoneyPlatformPipeline.IsUserScopedAccessToken(""));
    }

    [Fact]
    public void TryGetJwtExpiry_ExtractsExpClaim()
    {
        long unix = FutureUnix(daysFromNow: 7);
        string jwt = MakeJwt(new { sub = "x", aud = "user", exp = unix });

        DateTime? actual = MoneyPlatformPipeline.TryGetJwtExpiry(jwt);

        Assert.NotNull(actual);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, actual!.Value);
    }

    [Fact]
    public async Task RunAsync_HappyPath_DiscoversEndpointAndUploadsAndReportsCompletedLink()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", role = "registered", name = "u@example.com", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-36.filestore.app/upload","params":"OPAQUE-BUNDLE"},"signature":"SIG-128CHARS"}"""));
        HttpResponseSnapshot uploadResp = Json("""{"status":"success","success":true,"status_code":200,"user_file_id":"b06d4b8fe1b0c","link":"https://fboom.me/file/b06d4b8fe1b0c"}""");

        IReadOnlyDictionary<string, string>? capturedHeaders = null;
        IReadOnlyDictionary<string, string>? capturedFields = null;
        string? capturedEndpoint = null;
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, headers) =>
            {
                // The pre-flight storage check fires first; return a generous quota so it
                // passes, then capture headers on the actual upload-url GET.
                if (url.Contains("/users/me/statistic", StringComparison.Ordinal))
                {
                    return Task.FromResult(Json("""{"storageSpace":{"total":10737418240,"used":0},"dailyTraffic":{"total":0,"used":0},"downloadedTotal":0}"""));
                }
                capturedHeaders = headers;
                return Task.FromResult(getResponses.Dequeue());
            },
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                capturedEndpoint = endpoint;
                capturedFields = fields;
                return Task.FromResult(uploadResp);
            });

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://fboom.me/file/b06d4b8fe1b0c", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(getResponses);

        // Cookie header carries the JWT.
        Assert.NotNull(capturedHeaders);
        Assert.Contains($"accessToken={jwt}", capturedHeaders!["Cookie"], StringComparison.Ordinal);

        Assert.Equal("https://prx-36.filestore.app/upload", capturedEndpoint);
    }

    [Fact]
    public async Task RunAsync_DiscoveryTransient5xxThenSuccess_RetriesAndUploads()
    {
        // FileBoom's /v1/files/upload-url intermittently 500s (empty body) or 524s
        // (Cloudflare origin timeout). The discovery GET is idempotent, so the pipeline
        // retries with backoff. First two attempts fail transiently, third succeeds.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(new HttpResponseSnapshot(500, string.Empty, []));   // attempt 1: transient
        getResponses.Enqueue(new HttpResponseSnapshot(524, "error code: 524", [])); // attempt 2: Cloudflare timeout
        getResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P"},"signature":"S"}"""));

        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/ok"}""")))
        {
            DiscoveryRetryBackoffMs = [1, 1], // don't actually sleep seconds in the test
        };

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://fboom.me/file/ok", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(getResponses); // all three discovery responses consumed (2 retries + success)
    }

    [Fact]
    public async Task RunAsync_DiscoveryAll5xx_FailsAfterRetriesWithClearMessage()
    {
        // Every discovery attempt 5xxs — after exhausting retries the upload fails with a
        // "temporarily unavailable" message (and the bytes are never sent).
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(new HttpResponseSnapshot(500, string.Empty, []));
        getResponses.Enqueue(new HttpResponseSnapshot(500, string.Empty, []));
        getResponses.Enqueue(new HttpResponseSnapshot(524, "error code: 524", []));

        bool uploadCalled = false;
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => { uploadCalled = true; return Task.FromResult(Json("{}")); })
        {
            DiscoveryRetryBackoffMs = [1, 1], // 2 retries → 3 attempts total
        };

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.False(uploadCalled, "bytes must not be sent when discovery never succeeds");
        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("temporarily unavailable", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 attempt", failed.Reason, StringComparison.Ordinal); // initial + 2 retries
        Assert.Empty(getResponses);
    }

    [Fact]
    public async Task RunAsync_Discovery4xx_DoesNotRetry()
    {
        // A 4xx (client error) is terminal — no retry, fail fast. Only ONE discovery
        // response is queued; a retry would dequeue an empty queue and throw.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(new HttpResponseSnapshot(400, """{"message":"bad request"}""", []));

        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run"))
        {
            DiscoveryRetryBackoffMs = [1, 1],
        };

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(getResponses); // exactly one attempt, no retry
    }

    [Fact]
    public async Task RunAsync_StoragePreCheck_FileExceedsAvailable_FailsWithoutDiscoveryOrUpload()
    {
        // Pre-flight check: the account has only 50 bytes free but the file is 100 bytes, so
        // the upload fails up front with a clear, actionable message — never calling
        // /v1/files/upload-url or pushing any bytes.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        bool discoveryCalled = false;
        bool uploadCalled = false;
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) =>
            {
                if (url.Contains("/users/me/statistic", StringComparison.Ordinal))
                {
                    return Task.FromResult(Json("""{"storageSpace":{"total":1000,"used":950},"dailyTraffic":{"total":0,"used":0},"downloadedTotal":0}"""));
                }
                discoveryCalled = true;
                return Task.FromResult(Json("{}"));
            },
            uploadOverride: (_, _, _, _, _) => { uploadCalled = true; return Task.FromResult(Json("{}")); });

        AttemptContext ctx = MakeContextWithCachedJwt(jwt); // FileSize = 100, only 50 free
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.False(discoveryCalled, "upload-url discovery must not run when the file can't fit");
        Assert.False(uploadCalled, "bytes must not be sent when the file can't fit");
        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Not enough FileBoom storage", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StoragePreCheck_FileFits_ProceedsToUpload()
    {
        // Pre-flight check passes (plenty of room) → discovery + upload proceed normally.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> uploadUrl = new();
        uploadUrl.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P"},"signature":"S"}"""));
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, uploadUrl, storageUsed: 0, storageTotal: 10_737_418_240)),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/ok"}""")));

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_Discovery403StorageLimit_FailsTerminallyWithoutReLogin()
    {
        // A 403 carrying a "Storage limit exceeded" message is a terminal upload failure,
        // NOT an expired token — the pipeline surfaces the message and must NOT evict the
        // cached auth or trigger a WebView re-login (the old code mistook every 403 for
        // auth-expired). The pre-check returns "unknown" here so the 403 backstop is what
        // we're exercising.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> uploadUrl = new();
        uploadUrl.Enqueue(new HttpResponseSnapshot(403, """{"message":"Storage limit exceeded"}""", []));
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, uploadUrl, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("bytes must not be sent on a storage-limit 403"));

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Storage limit exceeded", failed.Reason, StringComparison.Ordinal);
        // NOT an auth failure → no re-login, cached creds preserved.
        Assert.Empty(events.OfType<AuthFailed>());
        Assert.Equal(jwt, ctx.Credentials.SessionCookie);
    }

    [Fact]
    public async Task RunAsync_MultipartFieldsCarrySignatureParamsAjaxAndQqMetadata()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"ENC"},"signature":"HEX"}"""));

        IReadOnlyDictionary<string, string>? captured = null;
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, fields, _, _) =>
            {
                captured = fields;
                return Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/x"}"""));
            });

        AttemptContext ctx = MakeContextWithCachedJwt(jwt) with { FileName = "movie.avi", FileSize = 5_225_142L };
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx, CancellationToken.None))
        { }

        Assert.NotNull(captured);
        Assert.Equal("HEX", captured!["signature"]);
        Assert.Equal("ENC", captured["params"]);
        Assert.Equal("true", captured["ajax"]);
        Assert.True(Guid.TryParse(captured["qquuid"], out _));
        Assert.Equal("movie.avi", captured["qqfilename"]);
        Assert.Equal("5225142", captured["qqtotalfilesize"]);

        // Insertion order MUST match the wire shape — Dictionary<string,string> preserves it.
        string[] expectedOrder = ["signature", "params", "ajax", "qquuid", "qqfilename", "qqtotalfilesize"];
        Assert.Equal(expectedOrder, captured.Keys.ToArray());
    }

    [Fact]
    public async Task RunAsync_DiscoveryReturns401_EvictsCacheAndYieldsAuthFailed()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(new HttpResponseSnapshot(401, """{"error":"Unauthorized"}""", []));

        bool uploadCalled = false;
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => { uploadCalled = true; return Task.FromResult(Json("{}")); });

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.False(uploadCalled, "Upload must not run when discovery 401s");
        AuthFailed af = Assert.Single(events.OfType<AuthFailed>());
        Assert.Contains("expired", af.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(events, e => e is AttemptFailed);

        // Persisted credentials are cleared so the next attempt re-runs the WebView.
        Assert.Null(ctx.Credentials.SessionCookie);
        Assert.Null(ctx.Credentials.SessionCookieExpiresUtc);
        Assert.Null(ctx.Credentials.PinnedProxyId);
    }

    [Fact]
    public async Task RunAsync_NoCachedAuthAndAuthServicePresent_SignsInViaWebViewAndUploads()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "u@example.com", role = "registered", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["pcId"] = "PCID-VAL" }));

        Queue<HttpResponseSnapshot> uploadUrlResponses = new();
        uploadUrlResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-7.filestore.app/upload","params":"ENC"},"signature":"SIG"}"""));

        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, uploadUrlResponses, storageUsed: 695056440, storageTotal: 10737418240)),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/zzz"}""")));

        AttemptContext ctx = MakeContext(); // no cached cookie
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(1, fake.CallCount);
        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://fboom.me/file/zzz", tc.FileUrl);

        // DTO captured the JWT + pcId for next time.
        Assert.Equal(jwt, ctx.Credentials.SessionCookie);
        Assert.Equal("PCID-VAL", ctx.Credentials.ApiKey);
        Assert.NotNull(ctx.Credentials.SessionCookieExpiresUtc);

        // Storage stats were also captured from /v1/users/me/statistic.
        Assert.Equal(695056440L, ctx.Credentials.StorageUsedBytes);
        Assert.Equal(10737418240L, ctx.Credentials.StorageQuotaBytes);
    }

    [Fact]
    public async Task RunAsync_WebViewReturnsNull_YieldsAuthFailedFailFast()
    {
        FakeAuthService fake = new(null);
        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("GET must not run when sign-in fails"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("Upload must not run when sign-in fails"));

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_NoAuthServiceAndNoCachedCookie_YieldsAttemptFailedFast()
    {
        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("GET must not run without auth"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("Upload must not run without auth"));

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("interactive sign-in", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_CachedCookieFromDifferentProxy_RejectsAndRetriggersWebView()
    {
        // PinnedProxyId mismatch: the persisted cookie was issued by proxy 7, this
        // attempt rides proxy 9. The JWT may be IP-bound; treat the cache as stale.
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        string freshJwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(freshJwt, null, null));

        Queue<HttpResponseSnapshot> uploadUrlResponses = new();
        uploadUrlResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P"},"signature":"S"}"""));

        IReadOnlyDictionary<string, string>? capturedUploadUrlHeaders = null;
        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (url, headers) =>
            {
                if (url.Contains("/upload-url", StringComparison.Ordinal))
                {
                    capturedUploadUrlHeaders = headers;
                }

                return Task.FromResult(RouteGet(url, uploadUrlResponses, storageUsed: null, storageTotal: null));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/y"}""")));

        AttemptContext ctx = MakeContextWithCachedJwt(jwt, pinnedProxyId: 7);
        ctx = ctx with { Proxy = new ProxyChoice(9, null, "https://other.example:8080") };

        await foreach (UploadEvent _ in pipeline.RunAsync(ctx, CancellationToken.None))
        { }

        Assert.Equal(1, fake.CallCount);
        // Cookie header carries the FRESH jwt, not the stale one.
        Assert.NotNull(capturedUploadUrlHeaders);
        Assert.Contains($"accessToken={freshJwt}", capturedUploadUrlHeaders!["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccountAsync_OnWebViewSuccess_ReturnsValidWithEmailFromJwt()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "alice@example.com", role = "registered", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));
        FileBoomPipeline pipeline = new(authService: fake, loginRepository: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal("alice@example.com", result.DerivedUsername);
        Assert.Equal(jwt, result.SessionCookie);
        Assert.NotNull(result.SessionCookieExpiresUtc);
    }

    [Fact]
    public async Task CheckAccountAsync_WithStatisticOverride_ReturnsStorageUsedAndQuota()
    {
        // Verifies the /v1/users/me/statistic round-trip flows through to
        // AccountCheckResult.StorageUsedBytes / StorageQuotaBytes and into Message.
        // Real wire shape: {"storageSpace":{"total":10737418240,"used":695056440}, ...}
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "u@example.com", role = "registered", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));
        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(
                url.Contains("/users/me/statistic", StringComparison.Ordinal)
                    ? Json("""{"storageSpace":{"total":10737418240,"used":695056440},"dailyTraffic":{"total":0,"used":0},"downloadedTotal":0}""")
                    : throw new InvalidOperationException($"Unexpected GET: {url}")),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("Upload must not run"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(695056440L, result.StorageUsedBytes);
        Assert.Equal(10737418240L, result.StorageQuotaBytes);
        // Storage is surfaced via dedicated Used/Available columns, NOT via Message —
        // Message stays operational ("Logged in" for free, "Premium until ..." for premium).
        Assert.Equal("Logged in", result.Message);
    }

    [Fact]
    public async Task CheckAccountAsync_WhenStatisticFails_StillReturnsValidWithoutQuota()
    {
        // Statistic failure must not break the Refresh path — quota is opportunistic.
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "u@example.com", role = "registered", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));
        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(500, "Internal Error", [])),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("Upload must not run"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
        Assert.Equal("Logged in", result.Message);
    }

    [Fact]
    public async Task CheckAccountAsync_OnPremiumJwt_ReportsPremium()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "bob@example.com", role = "premium", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));
        FileBoomPipeline pipeline = new(authService: fake, loginRepository: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "bob@example.com", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
    }

    [Fact]
    public async Task CheckAccountAsync_OnWebViewCancel_ReturnsInvalid()
    {
        FakeAuthService fake = new(null);
        FileBoomPipeline pipeline = new(authService: fake, loginRepository: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_WithoutAuthService_ReturnsInvalidFailFast()
    {
        FileBoomPipeline pipeline = new(authService: null, loginRepository: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "u", password: "p", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("interactive sign-in", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_UploadResponseIsErrorEnvelope_YieldsAttemptFailedWithMessage()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> getResponses = new();
        getResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P"},"signature":"S"}"""));

        FileBoomPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, getResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"error","success":false,"message":"Storage node rejected"}""")));

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Storage node rejected", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SecondAttemptSameCredentials_ReusesAuthAndSkipsWebView()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));

        Queue<HttpResponseSnapshot> uploadUrlResponses = new();
        uploadUrlResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P1"},"signature":"S1"}"""));
        uploadUrlResponses.Enqueue(Json("""{"endpoint":{"url":"https://prx-1.filestore.app/upload","params":"P2"},"signature":"S2"}"""));

        FileBoomPipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (url, _) => Task.FromResult(RouteGet(url, uploadUrlResponses, storageUsed: null, storageTotal: null)),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(Json("""{"status":"success","success":true,"link":"https://fboom.me/file/x"}""")));

        FileHosterLoginDto creds = new() { Id = 99, FileHosterName = "FileBoom", Username = "u" };
        AttemptContext c1 = MakeContext() with { Credentials = creds, FileName = "a.bin", FilePath = @"C:\pkg\a.bin" };
        AttemptContext c2 = MakeContext() with { Credentials = creds, FileName = "b.bin", FilePath = @"C:\pkg\b.bin" };

        await foreach (UploadEvent _ in pipeline.RunAsync(c1, CancellationToken.None))
        { }
        await foreach (UploadEvent _ in pipeline.RunAsync(c2, CancellationToken.None))
        { }

        Assert.Equal(1, fake.CallCount); // WebView opens once across BOTH files
        Assert.Empty(uploadUrlResponses);
    }

    // ---- helpers ----

    /// <summary>Builds a syntactically-valid JWT (3 base64url segments). The header and
    /// signature are placeholders — only the payload claims matter for our decoder.</summary>
    private static string MakeJwt(object payloadObject)
    {
        const string fixedHeader = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9";
        string payloadJson = JsonSerializer.Serialize(payloadObject);
        string payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        return $"{fixedHeader}.{payloadBase64}.fakesig";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long FutureUnix(int daysFromNow = 1)
        => DateTimeOffset.UtcNow.AddDays(daysFromNow).ToUnixTimeSeconds();

    private static HttpResponseSnapshot Json(string body) => new(200, body, []);

    /// <summary>
    /// Multiplex GET responses for tests that drive BOTH /v1/files/upload-url AND
    /// /v1/users/me/statistic. The statistic call is opportunistic and fires on every
    /// fresh WebView sign-in; tests prove behaviour with canned (used, total) values
    /// or pass nulls to stub it as "unavailable".
    /// </summary>
    private static HttpResponseSnapshot RouteGet(string url, Queue<HttpResponseSnapshot> uploadUrl, long? storageUsed, long? storageTotal)
    {
        if (url.Contains("/users/me/statistic", StringComparison.Ordinal))
        {
            if (storageUsed is null || storageTotal is null)
            {
                return new HttpResponseSnapshot(500, "", []);
            }
            return Json($$"""{"storageSpace":{"total":{{storageTotal}},"used":{{storageUsed}}},"dailyTraffic":{"total":0,"used":0},"downloadedTotal":0}""");
        }
        return uploadUrl.Dequeue();
    }

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\pkg\x.bin",
        FileName = "x.bin",
        FileSize = 100,
        FileHash = null,
        HosterName = "FileBoom",
        Credentials = new FileHosterLoginDto { Id = 17, FileHosterName = "FileBoom", Username = "u@example.com", Password = "" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    /// <summary>Context whose credentials carry a pre-cached JWT + matching proxy pin,
    /// so EnsureAuthAsync rehydrates without invoking the (absent) WebView.</summary>
    private static AttemptContext MakeContextWithCachedJwt(string jwt, int pinnedProxyId = 0)
    {
        AttemptContext baseCtx = MakeContext();
        DateTime? exp = MoneyPlatformPipeline.TryGetJwtExpiry(jwt);
        baseCtx.Credentials.SessionCookie = jwt;
        baseCtx.Credentials.SessionCookieExpiresUtc = exp ?? DateTime.UtcNow.AddDays(1);
        baseCtx.Credentials.PinnedProxyId = pinnedProxyId;
        return baseCtx;
    }

    private sealed class FakeAuthService(InteractiveAuthResult? result) : IInteractiveAuthService
    {
        public int CallCount { get; private set; }

        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
            InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
