// <copyright file="DDownloadPipelineTests.cs" company="CSUploader">
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
/// DDownload on the family's API-key path. The protocol is entirely the base's, so what's worth pinning
/// is the one thing that is NOT: its API answers only on <c>api-v2.ddownload.com</c> while links and
/// account pages stay on <c>ddownload.com</c>. Get that split wrong and every request lands on an HTML
/// page. Shapes below are the live responses (2026-08-01), session and key values replaced.
/// </summary>
public class DDownloadPipelineTests
{
    // GET /api/upload/server?key=… — note sess_id is a long API session, NOT the xfss cookie.
    private const string UploadServerJson =
        """{"msg":"OK","result":"https://eu-orion9.zeuscdn.org/cgi-bin/upload.cgi","sess_id":"api_sess_demo","status":200}""";

    [Fact]
    public async Task RunAsync_ApiKey_ResolvesTheNodeFromTheApiHost_ThenPostsTheFamilyFieldSet()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        DDownloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadServerJson); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"s4vkjl4k4kgc","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(new FileHosterLoginDto { Id = 1, FileHosterName = "DDownload", ApiKey = "demo_key" }), CancellationToken.None));

        // Link comes off the PAGE host, not the api host.
        Assert.Equal("https://ddownload.com/s4vkjl4k4kgc", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // …while the node lookup goes to the API host, which is the whole deviation.
        string lookup = Assert.Single(getUrls);
        Assert.StartsWith("https://api-v2.ddownload.com/api/upload/server", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://ddownload.com/api/", lookup, StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://eu-orion9.zeuscdn.org/cgi-bin/upload.cgi", call.Endpoint);
        Assert.Equal("api_sess_demo", call.ExtraFields["sess_id"]); // the API session, not a cookie
        Assert.Equal("reg", call.ExtraFields["utype"]);
        Assert.Equal("Start upload", call.ExtraFields["upload"]);
    }

    [Fact]
    public void DDownload_IsAnApiKeyHoster_WithNoClientSideCap()
    {
        DDownloadPipeline pipeline = new();
        Assert.Equal("DDownload", pipeline.Name);

        // The host declares no per-file limit anywhere, so guessing one would reject files the server
        // would have accepted. Null = let the server decide.
        Assert.Null(pipeline.MaxFileSize);

        // Account-only (the family default) — no anonymous option is offered.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("DDownload"));
        Assert.Equal("ddownload.com", FileHosterClient.FileHosters["DDownload"]);
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("DDownload"));
    }

    [Fact]
    public async Task CheckAccount_WithoutAPastedKey_SaysWhereToGetItInsteadOfAttemptingSignIn()
    {
        // DDownload's modernised dashboard dropped the api-url input, so the family bootstrap can only
        // ever end in "my_account did not contain an api-url input after generate" — which reads like
        // a parser bug. Fail immediately with the actual remedy instead. Note authService is null: if
        // this ever tried to sign in, it would report "Sign-in service unavailable" and this fails.
        DDownloadPipeline pipeline = new();
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: "someone", password: "secret", apiKey: null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Affiliate", result.Message, StringComparison.OrdinalIgnoreCase);   // where it lives
        Assert.Contains("Paste", result.Message, StringComparison.OrdinalIgnoreCase);       // what to do
        Assert.DoesNotContain("api-url", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sign-in service unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutAnApiKey_FailsWithTheSameGuidance_NotAScrapeError()
    {
        DDownloadPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not call my_account"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(new FileHosterLoginDto { Id = 1, FileHosterName = "DDownload", Username = "someone" }), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Affiliate", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
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

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 4096,
        HosterName = "DDownload",
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
}
