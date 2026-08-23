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
                    // /accounts returns both the token and the rootFolder together (verified live).
                    return Ok("""{"status":"ok","data":{"id":"acc1","tier":"guest","token":"GUEST_TOKEN","rootFolder":"ROOT-FOLDER-ID"}}""");
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

        // Two API steps: unauthenticated account creation, then the Bearer-authed folder create.
        Assert.Equal(2, api.Count);
        Assert.Equal("POST", api[0].Method);
        Assert.EndsWith("/accounts", api[0].Url, StringComparison.Ordinal);
        Assert.Null(api[0].Bearer);                                   // account creation is unauthenticated
        Assert.EndsWith("/contents/createfolder", api[1].Url, StringComparison.Ordinal);
        Assert.Equal("GUEST_TOKEN", api[1].Bearer);                  // token from step 1
        Assert.Contains("ROOT-FOLDER-ID", api[1].Json!, StringComparison.Ordinal); // parentFolderId = rootFolder
        Assert.Contains("\"public\":true", api[1].Json!, StringComparison.Ordinal);

        // The upload carried the token + the created folder id.
        Dictionary<string, string> fields = Assert.Single(uploadFields);
        Assert.Equal("GUEST_TOKEN", fields["token"]);
        Assert.Equal("NEW-FOLDER-ID", fields["folderId"]);
    }

    [Fact]
    public async Task RunAsync_TransientAccount502_RetriesThenSucceeds()
    {
        // gofile's guest API 502s under load; the setup steps retry transient gateway failures.
        int accountsCalls = 0;
        GofilePipeline pipeline = new(
            api: (method, url, json, bearer) =>
            {
                if (url.EndsWith("/accounts", StringComparison.Ordinal))
                {
                    accountsCalls++;
                    return accountsCalls < 3
                        ? new HttpResponseSnapshot(502, "<html>502 Bad Gateway</html>", [])
                        : Ok("""{"status":"ok","data":{"token":"T","rootFolder":"R"}}""");
                }

                return Ok("""{"status":"ok","data":{"id":"F","code":"C"}}""");
            },
            upload: (_, _, _, _, _) => Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/C"}}"""));

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(3, accountsCalls); // 502, 502, ok
    }

    [Fact]
    public async Task RunAsync_StaleGuestAccount_MintsFreshAccountAndRetriesOnce()
    {
        // gofile purges inactive guest accounts server-side: createfolder against the purged account's
        // rootFolder comes back HTTP 200 + status "error-notFound" (the exact live failure). The pipeline
        // must drop the cached account, mint a fresh one, and succeed — not fail (nor keep failing on the
        // dead cache forever).
        int accountsCalls = 0;
        int createFolderCalls = 0;
        List<ApiCall> api = [];
        List<Dictionary<string, string>> uploadFields = [];
        GofilePipeline pipeline = new(
            api: (method, url, json, bearer) =>
            {
                api.Add(new ApiCall(method.Method, url, json, bearer));
                if (url.EndsWith("/accounts", StringComparison.Ordinal))
                {
                    accountsCalls++;
                    return Ok("{\"status\":\"ok\",\"data\":{\"token\":\"T" + accountsCalls + "\",\"rootFolder\":\"R" + accountsCalls + "\"}}");
                }

                // First createfolder: the cached account was purged → gofile's HTTP-200 notFound envelope.
                return ++createFolderCalls == 1
                    ? Ok("""{"status":"error-notFound","data":{}}""")
                    : Ok("""{"status":"ok","data":{"id":"F2","code":"C2"}}""");
            },
            upload: (_, _, fields, _, _) =>
            {
                uploadFields.Add(new Dictionary<string, string>(fields));
                return Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/C2"}}""");
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, accountsCalls);                       // stale account dropped → fresh one minted
        Assert.Equal(2, createFolderCalls);
        Assert.Equal("T2", api.Last(c => c.Url.EndsWith("/contents/createfolder", StringComparison.Ordinal)).Bearer);
        Assert.Equal("T2", Assert.Single(uploadFields)["token"]); // the upload rides the FRESH account
    }

    [Fact]
    public async Task RunAsync_FreshGuestAccountAlsoRejected_FailsWithClearError_NoLoop()
    {
        // If even the freshly minted account is rejected, fail with a clear error after exactly one
        // refresh — never loop.
        int accountsCalls = 0;
        GofilePipeline pipeline = new(
            api: (method, url, json, bearer) =>
            {
                if (url.EndsWith("/accounts", StringComparison.Ordinal))
                {
                    accountsCalls++;
                    return Ok("""{"status":"ok","data":{"token":"T","rootFolder":"R"}}""");
                }

                return Ok("""{"status":"error-notFound","data":{}}""");
            },
            upload: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run"));

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("rejected the guest account", fail.Reason, StringComparison.Ordinal);
        Assert.Equal(2, accountsCalls); // one stale refresh, then stop
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_ReusesGuestAccountAcrossUploads()
    {
        int accountsCalls = 0;
        GofilePipeline pipeline = new(
            api: (method, url, json, bearer) =>
            {
                if (url.EndsWith("/accounts", StringComparison.Ordinal))
                {
                    accountsCalls++;
                    return Ok("""{"status":"ok","data":{"token":"T","rootFolder":"R"}}""");
                }

                return Ok("""{"status":"ok","data":{"id":"F","code":"C"}}""");
            },
            upload: (_, _, _, _, _) => Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/C"}}"""));

        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // ONE guest account created and reused for the second upload (avoids gofile's per-IP rate limit).
        Assert.Equal(1, accountsCalls);
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
    public async Task RunAsync_Upload502_RetriesThenSucceeds()
    {
        // gofile's edge intermittently 502s the upload POST ("Error forwarding request to upload server")
        // even when setup succeeded — the upload step retries transient gateway verdicts like the API steps.
        int uploadCalls = 0;
        GofilePipeline pipeline = new(
            api: (_, url, _, _) => Ok(SetupResponse(url)),
            upload: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return uploadCalls < 3
                    ? new HttpResponseSnapshot(502, "Error forwarding request to upload server", [])
                    : Ok("""{"status":"ok","data":{"downloadPage":"https://gofile.io/d/C"}}""");
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://gofile.io/d/C", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(3, uploadCalls); // 502, 502, ok
    }

    [Fact]
    public async Task RunAsync_UploadPersistent502_FailsAfterBoundedRetries()
    {
        int uploadCalls = 0;
        GofilePipeline pipeline = new(
            api: (_, url, _, _) => Ok(SetupResponse(url)),
            upload: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return new HttpResponseSnapshot(502, "Error forwarding request to upload server", []);
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("HTTP 502", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Equal(4, uploadCalls); // the initial send + 3 bounded retries, then a terminal verdict
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
        url.EndsWith("/accounts", StringComparison.Ordinal)
            ? """{"status":"ok","data":{"token":"T","rootFolder":"R"}}"""
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
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };
}
