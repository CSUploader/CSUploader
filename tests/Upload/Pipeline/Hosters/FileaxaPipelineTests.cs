// <copyright file="FileaxaPipelineTests.cs" company="CSUploader">
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
/// FILEAXA is a plain <see cref="XFileSharingApiPipeline"/> shim, so the protocol is already covered
/// by the base's tests. What's pinned here is the wiring and the one decision the shim makes — the
/// cap — because the base's 1 GiB DEFAULT is a silent trap for any host bigger than that.
/// </summary>
public class FileaxaPipelineTests
{
    private const string UploadServerOkJson = """{"msg":"OK","status":200,"sess_id":"sess_fa","result":"https://s01.fileaxa.com/cgi-bin/upload.cgi"}""";
    private const string UploadOkJson = """[{"file_code":"abc123xyz","file_status":"OK"}]""";

    [Fact]
    public async Task RunAsync_WithApiKey_ResolvesTheServerAndUploads()
    {
        Queue<string> gets = new([UploadServerOkJson]);
        List<string> endpoints = [];

        FileaxaPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { endpoints.Add(url); return Task.FromResult(gets.Dequeue()); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://fileaxa.com/abc123xyz", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.StartsWith("https://fileaxa.com/api/upload/server?key=", Assert.Single(endpoints), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LargeFile_IsNotSkippedByTheBasesDefaultCap()
    {
        // The base defaults MaxFileSize to 1 GiB. Inheriting that on a host advertised at ~10 GB would
        // silently refuse anything larger BEFORE any request — a wrong "File exceeds …" rather than a
        // real answer from the server. The shim overrides it to null on purpose.
        Assert.Null(new FileaxaPipeline().MaxFileSize);

        Queue<string> gets = new([UploadServerOkJson]);
        FileaxaPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(gets.Dequeue()),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        AttemptContext ctx = MakeContext() with { FileSize = 4L * 1024 * 1024 * 1024 }; // 4 GiB
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Single(events.OfType<TransferCompleted>());
    }

    [Fact]
    public void Fileaxa_IsAccountOnly_OnTheApiKeyCredentialUi()
    {
        FileaxaPipeline pipeline = new();
        Assert.Equal("FILEAXA", pipeline.Name);

        // No utype=anon form on its homepage (checked live, and in the tier-A1 sweep).
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("FILEAXA"));
        Assert.Equal("fileaxa.com", FileHosterClient.FileHosters["FILEAXA"]);
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("FILEAXA"));
    }

    [Fact]
    public void LoginPage_UsesTheFamilyDefault()
    {
        // Unlike the Uploadrar fork, /login.html serves a 200 here — so this shim must NOT override
        // the route. Pinned so nobody copies Uploadrar's override across by pattern-matching.
        object? path = typeof(XFileSharingApiPipeline)
            .GetProperty("LoginPagePath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(new FileaxaPipeline());

        Assert.Equal("/login.html", path);
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

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\release.r00",
        FileName = "release.r00",
        FileSize = 4096,
        HosterName = "FILEAXA",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "FILEAXA", ApiKey = "key_fa" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
