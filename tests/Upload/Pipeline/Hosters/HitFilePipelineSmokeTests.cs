// <copyright file="HitFilePipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>Config sanity check for <see cref="HitFilePipeline"/> — anonymous-only for now.</summary>
public class HitFilePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareHitFileAnonymousConfig()
    {
        HitFilePipeline pipeline = new();
        Assert.Equal("HitFile", pipeline.Name);
        // No anonymous cap is advertised by the API; the server enforces its own limit.
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
        Assert.True(pipeline.SupportsAnonymousUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        HitFilePipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Fact]
    public async Task CheckAccountAsync_NoAuthServiceAndNoKey_ReturnsClearFailure()
    {
        // Sign-in needs the WebView (Turnstile captcha). With no auth service (the parameterless
        // ctor / unit-test path) and no pasted key, it must fail clearly rather than throw.
        HitFilePipeline pipeline = new();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", "pass", apiKey: null,
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.ApiKey);
    }

    [Fact]
    public async Task CheckAccountAsync_PastedAppId_AcceptedAsCredential()
    {
        // The EditAccount UI also lets the user paste an appId directly; accept it as the
        // credential (HitFile exposes no appId-validation endpoint — the upload is its test).
        HitFilePipeline pipeline = new();
        const string AppId = "D2A1336FBEB989D9692A02F45EC60F59";

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", "pass", apiKey: "  " + AppId + "  ",
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AppId, result.ApiKey); // trimmed
    }

    [Fact]
    public void ParseProbeResult_AppIdUsedBytesAndUsername_ParsesAll()
    {
        (string? appId, long? used, string? username) = HitFilePipeline.ParseProbeResult(
            """{"appId":"BgDTTjq","usedBytes":15663360,"username":"  user@example.com  "}""");

        Assert.Equal("BgDTTjq", appId);
        Assert.Equal(15663360L, used);
        Assert.Equal("user@example.com", username); // trimmed
    }

    [Fact]
    public void ParseProbeResult_NullUsedBytes_ReturnsAppIdWithNullUsage()
    {
        // The probe returns usedBytes:null when the storage walk fails/stalls — sign-in still
        // succeeds (account valid), just without a usage figure.
        (string? appId, long? used, _) = HitFilePipeline.ParseProbeResult("""{"appId":"BgDTTjq","usedBytes":null}""");

        Assert.Equal("BgDTTjq", appId);
        Assert.Null(used);
    }

    [Theory]
    [InlineData("""{"appId":"BgDTTjq","usedBytes":1}""")] // username key absent
    [InlineData("""{"appId":"BgDTTjq","usedBytes":1,"username":null}""")] // x-logged-in header wasn't exposed
    [InlineData("""{"appId":"BgDTTjq","usedBytes":1,"username":""}""")] // present but empty
    public void ParseProbeResult_MissingOrEmptyUsername_ReturnsNullUsername(string payload)
    {
        (string? appId, _, string? username) = HitFilePipeline.ParseProbeResult(payload);

        Assert.Equal("BgDTTjq", appId);
        Assert.Null(username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"usedBytes":123}""")] // no appId
    public void ParseProbeResult_MissingOrGarbage_ReturnsNullAppId(string payload)
    {
        (string? appId, _, _) = HitFilePipeline.ParseProbeResult(payload);

        Assert.Null(appId);
    }
}
