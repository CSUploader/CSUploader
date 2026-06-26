// <copyright file="Keep2SharePipelineTests.cs" company="CSUploader">
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

/// <summary>
/// Keep2Share-specific tests. The full upload/auth/storage protocol is shared with FileBoom via
/// <see cref="MoneyPlatformPipeline"/> and exercised by FileBoomPipelineTests; these lock in that
/// Keep2Share routes to the keep2share.cc domains and surfaces the k2s.cc share link.
/// </summary>
public class Keep2SharePipelineTests
{
    [Fact]
    public void Properties_DeclareKeep2ShareConfigAndMatchRegistry()
    {
        Keep2SharePipeline pipeline = new();

        Assert.Equal("Keep2Share", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize); // free per-file cap pending; base pre-flight quota check still guards
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Fact]
    public async Task RunAsync_HappyPath_HitsKeep2ShareApiAndReturnsK2sLink()
    {
        string jwt = MakeJwt(new { sub = "1", aud = "user", name = "u@example.com", role = "registered", exp = FutureUnix() });
        Queue<HttpResponseSnapshot> uploadUrl = new();
        uploadUrl.Enqueue(Json("""{"endpoint":{"url":"https://prx-17.filestore.app/upload","params":"PARAMS"},"signature":"SIG"}"""));
        HttpResponseSnapshot uploadResp = Json("""{"status":"success","success":true,"status_code":200,"user_file_id":"08809c25d338a","link":"https://k2s.cc/file/08809c25d338a"}""");

        string? discoveryUrl = null;
        IReadOnlyDictionary<string, string>? discoveryHeaders = null;
        IReadOnlyDictionary<string, string>? uploadHeaders = null;
        Keep2SharePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, headers) =>
            {
                if (url.Contains("/users/me/statistic", StringComparison.Ordinal))
                {
                    return Task.FromResult(Json("""{"storageSpace":{"total":10737418240,"used":0},"dailyTraffic":{"total":10737418240,"used":0},"downloadedTotal":0}"""));
                }
                discoveryUrl = url;
                discoveryHeaders = headers;
                return Task.FromResult(uploadUrl.Dequeue());
            },
            uploadOverride: (_, _, _, headers, _) =>
            {
                uploadHeaders = headers;
                return Task.FromResult(uploadResp);
            });

        AttemptContext ctx = MakeContextWithCachedJwt(jwt);
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://k2s.cc/file/08809c25d338a", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Discovery hit the keep2share API host with the accessToken cookie.
        Assert.Equal("https://api.keep2share.cc/v1/files/upload-url", discoveryUrl);
        Assert.Contains($"accessToken={jwt}", discoveryHeaders!["Cookie"], StringComparison.Ordinal);
        // The upload POST carries the keep2share Origin (the signed bundle is the auth, no cookie).
        Assert.Equal("https://keep2share.cc", uploadHeaders!["Origin"]);
        Assert.Equal("https://keep2share.cc/", uploadHeaders["Referer"]);
    }

    [Fact]
    public async Task CheckAccountAsync_OnWebViewSuccess_ReturnsEmailAndStorageFromKeep2ShareApi()
    {
        string jwt = MakeJwt(new { sub = "69480768", aud = "user", name = "nirav@example.net", role = "registered", exp = FutureUnix() });
        FakeAuthService fake = new(new InteractiveAuthResult(jwt, null, null));
        string? statUrl = null;
        Keep2SharePipeline pipeline = new(
            authService: fake,
            loginRepository: null,
            getOverride: (url, _) =>
            {
                statUrl = url;
                return Task.FromResult(Json("""{"storageSpace":{"total":10737418240,"used":12345},"dailyTraffic":{"total":10737418240,"used":0},"downloadedTotal":0}"""));
            },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "", password: "", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal("nirav@example.net", result.DerivedUsername);
        Assert.Equal(jwt, result.SessionCookie);
        Assert.Equal(12345L, result.StorageUsedBytes);
        Assert.Equal(10737418240L, result.StorageQuotaBytes); // 10 GiB free quota
        Assert.Equal("https://api.keep2share.cc/v1/users/me/statistic", statUrl);
    }

    // ---- helpers (mirror FileBoomPipelineTests) ----

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

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\pkg\x.bin",
        FileName = "x.bin",
        FileSize = 100,
        FileHash = null,
        HosterName = "Keep2Share",
        Credentials = new FileHosterLoginDto { Id = 31, FileHosterName = "Keep2Share", Username = "u@example.com", Password = "" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static AttemptContext MakeContextWithCachedJwt(string jwt)
    {
        AttemptContext baseCtx = MakeContext();
        DateTime? exp = MoneyPlatformPipeline.TryGetJwtExpiry(jwt);
        baseCtx.Credentials.SessionCookie = jwt;
        baseCtx.Credentials.SessionCookieExpiresUtc = exp ?? DateTime.UtcNow.AddDays(1);
        baseCtx.Credentials.PinnedProxyId = 0;
        return baseCtx;
    }

    private sealed class FakeAuthService(InteractiveAuthResult? result) : IInteractiveAuthService
    {
        public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
            InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
