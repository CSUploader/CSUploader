// <copyright file="GofilePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
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
/// Orchestration tests for <see cref="GofilePipeline"/> — the guest-account → rootFolder →
/// createfolder → uploadfile flow. The JSON API and the multipart upload are stubbed (the wire shapes
/// come from the live capture + gofile's site JS; the real endpoints are the live test), so these lock
/// in the step sequence, the auth/field wiring, the share link, and the failure/retry branches.
/// </summary>
public class GofilePipelineTests
{
    private sealed record ApiCall(string Method, string Url, string? Json, string? Bearer);

    [Fact]
    public void Properties_DeclareGofileConfig()
    {
        GofilePipeline pipeline = new();
        Assert.Equal("Gofile", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Gofile"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_RunsTheFourStepsAndReturnsDownloadPage()
    {
        List<ApiCall> api = [];
        List<Dictionary<string, string>> uploadFields = [];
        GofilePipeline pipeline = new(
            api: (method, url, json, bearer) =>
            {
                api.Add(new ApiCall(method.Method, url, json, bearer));
                if (url.EndsWith("/accounts", StringComparison.Ordinal))
                {
                    return Ok("""{"status":"ok","data":{"token":"GUEST_TOKEN"}}""");
                }

                if (url.EndsWith("/accounts/website", StringComparison.Ordinal))
                {
                    return Ok("""{"status":"ok","data":{"rootFolder":"ROOT-FOLDER-ID","email":"guest123"}}""");
                }

                // createfolder
                return Ok("""{"status":"ok","data":{"id":"NEW-FOLDER-ID","code":"VCkQzq","type":"folder"}}""");
            },
            upload: (filePath, url, fields, headers, _) =>
            {
                uploadFields.Add(new Dictionary<string, string>(fields));
                return Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/VCkQzq","code":"VCkQzq","parentFolder":"NEW-FOLDER-ID"}}""");
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://gofile.io/d/VCkQzq", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The four steps in order, with the right method + auth.
        Assert.Equal(3, api.Count);
        Assert.Equal("POST", api[0].Method);
        Assert.EndsWith("/accounts", api[0].Url, StringComparison.Ordinal);
        Assert.Null(api[0].Bearer);                                   // account creation is unauthenticated
        Assert.Equal("GET", api[1].Method);
        Assert.EndsWith("/accounts/website", api[1].Url, StringComparison.Ordinal);
        Assert.Equal("GUEST_TOKEN", api[1].Bearer);                  // token from step 1
        Assert.EndsWith("/contents/createfolder", api[2].Url, StringComparison.Ordinal);
        Assert.Equal("GUEST_TOKEN", api[2].Bearer);
        Assert.Contains("ROOT-FOLDER-ID", api[2].Json!, StringComparison.Ordinal); // parentFolderId = rootFolder
        Assert.Contains("\"public\":true", api[2].Json!, StringComparison.Ordinal);

        // The upload carried the token + the created folder id.
        Dictionary<string, string> fields = Assert.Single(uploadFields);
        Assert.Equal("GUEST_TOKEN", fields["token"]);
        Assert.Equal("NEW-FOLDER-ID", fields["folderId"]);
    }

    [Fact]
    public async Task RunAsync_AccountCreationFails_YieldsAttemptFailedWithoutUpload()
    {
        bool uploadRan = false;
        GofilePipeline pipeline = new(
            api: (_, _, _, _) => new HttpResponseSnapshot(500, "server error", []),
            upload: (_, _, _, _, _) => { uploadRan = true; return Ok("""{"status":"ok","data":{"downloadPage":"x"}}"""); });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.False(uploadRan);
    }

    [Fact]
    public async Task RunAsync_ApiStatusNotOk_YieldsAttemptFailed()
    {
        // accounts returns 200 but a non-ok envelope → setup fails before upload.
        bool uploadRan = false;
        GofilePipeline pipeline = new(
            api: (_, url, _, _) => Ok("""{"status":"error-auth","data":{}}"""),
            upload: (_, _, _, _, _) => { uploadRan = true; return Ok("{}"); });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.False(uploadRan);
    }

    [Fact]
    public async Task RunAsync_UploadRejected_YieldsAttemptFailedWithoutCompletion()
    {
        GofilePipeline pipeline = new(
            api: (_, url, _, _) => Ok(SetupResponse(url)),
            upload: (_, _, _, _, _) => new HttpResponseSnapshot(200, """{"status":"error-notPremium","data":{}}""", []));

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("gofile.io upload", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send abort after setup must PROPAGATE (retryable) — no file was created, so the shared
        // retry layer re-runs against a fresh guest account.
        GofilePipeline pipeline = new(
            api: (_, url, _, _) => Ok(SetupResponse(url)),
            upload: (_, _, _, _, _) =>
                throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054)))));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    private static string SetupResponse(string url) =>
        url.EndsWith("/accounts", StringComparison.Ordinal) ? """{"status":"ok","data":{"token":"T"}}"""
        : url.EndsWith("/accounts/website", StringComparison.Ordinal) ? """{"status":"ok","data":{"rootFolder":"R"}}"""
        : """{"status":"ok","data":{"id":"F","code":"C"}}""";

    private static HttpResponseSnapshot Ok(string body) => new(200, body, []);

    private static async Task<List<UploadEvent>> Drain(IAsyncEnumerable<UploadEvent> stream)
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
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_000_000,
        HosterName = "Gofile",
        Credentials = new FileHosterLoginDto { FileHosterName = "Gofile", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
