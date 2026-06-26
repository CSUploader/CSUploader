// <copyright file="NitroFlarePipelineTests.cs" company="CSUploader">
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

public class NitroFlarePipelineTests
{
    private const string Hash = "beadaed4eb9c0a7f3d2e1b0a9c8d7e6f5a4b3c2d"; // 40-hex stand-in

    [Fact]
    public void Properties_DeclareNitroFlareConfigAndMatchRegistry()
    {
        NitroFlarePipeline pipeline = new();

        Assert.Equal("NitroFlare", pipeline.Name);
        Assert.Equal(10L * 1024 * 1024 * 1024, pipeline.MaxFileSize); // 10 GiB free per-file cap
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Theory]
    [InlineData("https://s92.nitroflare.com:8443/index.php", true)]
    [InlineData("https://nitroflare.com/x", true)]
    [InlineData("http://s92.nitroflare.com:8443/index.php", false)]  // not https
    [InlineData("https://evil.com/index.php", false)]                // wrong host
    [InlineData("https://nitroflare.com.evil.com/x", false)]         // suffix spoof
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public void IsValidUploadServer_AcceptsOnlyHttpsNitroflare(string url, bool expected)
        => Assert.Equal(expected, NitroFlarePipeline.IsValidUploadServer(url));

    [Fact]
    public void ParseProbeResult_ExtractsHashAndEmail()
    {
        (string? hash, string? email) = NitroFlarePipeline.ParseProbeResult($$"""{"hash":"{{Hash}}","email":"u@example.net"}""");
        Assert.Equal(Hash, hash);
        Assert.Equal("u@example.net", email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"email":"u@x.net"}""")] // no hash
    public void ParseProbeResult_MissingOrBadPayload_ReturnsNullHash(string probe)
        => Assert.Null(NitroFlarePipeline.ParseProbeResult(probe).Hash);

    [Fact]
    public async Task RunAsync_HappyPath_DiscoversServerAndUploadsWithUserHash()
    {
        HttpResponseSnapshot getServer = new(200, "https://s92.nitroflare.com:8443/index.php", []);
        HttpResponseSnapshot uploadResp = new(
            200,
            """{"files":[{"name":"x.avi","size":"5225142","type":"video/avi","xxhash":"abc","url":"https://nitroflare.com/view/BE570662E33EC7F/x.avi"}]}""",
            []);

        string? capturedEndpoint = null;
        IReadOnlyDictionary<string, string>? capturedFields = null;
        IReadOnlyDictionary<string, string>? capturedHeaders = null;
        NitroFlarePipeline pipeline = new(
            getOverride: _ => getServer,
            uploadOverride: (_, endpoint, fields, headers, _) =>
            {
                capturedEndpoint = endpoint;
                capturedFields = fields;
                capturedHeaders = headers;
                return Task.FromResult(uploadResp);
            });

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(Hash), CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://nitroflare.com/view/BE570662E33EC7F/x.avi", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        Assert.Equal("https://s92.nitroflare.com:8443/index.php", capturedEndpoint);
        Assert.Equal(Hash, capturedFields!["user"]);             // the hash authorizes the upload
        Assert.Equal("https://nitroflare.com", capturedHeaders!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_NoStoredHash_FailsBeforeAnyTransfer()
    {
        NitroFlarePipeline pipeline = new(
            getOverride: _ => throw new InvalidOperationException("getServer must not run without a hash"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run without a hash"));

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(apiKey: null), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("isn't signed in", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_GetServerReturnsGarbage_FailsBeforeAnyTransfer()
    {
        NitroFlarePipeline pipeline = new(
            getOverride: _ => new HttpResponseSnapshot(200, "Service temporarily unavailable", []),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run on a bad server"));

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(Hash), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>()); // never sent a byte
    }

    [Fact]
    public async Task RunAsync_GetServerSchemeDowngradedToHttp_FailsBeforeAnyTransfer()
    {
        // A valid host but http:// (a downgrade/MITM shape) must be rejected before any bytes — the
        // realistic spoof that IsValidUploadServer guards end-to-end.
        NitroFlarePipeline pipeline = new(
            getOverride: _ => new HttpResponseSnapshot(200, "http://s92.nitroflare.com:8443/index.php", []),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run on an http server"));

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(Hash), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_UploadErrorEnvelope_YieldsAttemptFailedWithMessage()
    {
        HttpResponseSnapshot getServer = new(200, "https://s92.nitroflare.com:8443/index.php", []);
        NitroFlarePipeline pipeline = new(
            getOverride: _ => getServer,
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, """{"files":[{"error":"file too big"}]}""", [])));

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(Hash), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("file too big", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccountAsync_WithStoredHash_StaysValidWithoutWebView()
    {
        NitroFlarePipeline pipeline = new(); // no auth service — must not be needed
        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u", "", apiKey: Hash, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(Hash, result.ApiKey);
    }

    private static AttemptContext MakeContext(string? apiKey) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\pkg\x.avi",
        FileName = "x.avi",
        FileSize = 5225142,
        FileHash = null,
        HosterName = "NitroFlare",
        Credentials = new FileHosterLoginDto { Id = 7, FileHosterName = "NitroFlare", Username = "u", ApiKey = apiKey },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
}
