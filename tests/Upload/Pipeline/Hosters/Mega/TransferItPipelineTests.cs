// <copyright file="TransferItPipelineTests.cs" company="CSUploader">
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
using CSUploader.Upload.Pipeline.Hosters.Mega;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Orchestration tests for <see cref="TransferItPipeline"/> — the glue over the MEGA helpers. The
/// MEGA API and the WebSocket upload are both stubbed (their internals are KAT-tested elsewhere; the
/// real wire is the live test), so these lock in the event sequence, the share URL, and error handling.
/// </summary>
public class TransferItPipelineTests
{
    [Fact]
    public void Properties_DeclareTransferItConfig()
    {
        TransferItPipeline pipeline = new();
        Assert.Equal("Transfer.it", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Transfer.it"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_UploadsAndReturnsTransferItLink()
    {
        MegaApi api = StubApi(
        [
            "[\"HANDLE\"]",                          // up
            null!,                                    // us → filled with a valid tsid below
            "[[0,[\"XHaaaaaaaaaa\",\"ROOThndl\"]]]",  // xn (status-wrapped)
            "[[[\"h1\",\"ul/u1\",0]]]",               // usc
            "[{\"f\":[{\"h\":\"NODEhndl\"}]}]",       // xp finalise
            "[0]",                                     // xc close
        ]);

        bool progressed = false;
        TransferItPipeline pipeline = new(
            _ => api,
            (pool, ctx, ulKey, progress, ct) =>
            {
                progress(ctx.FileSize, ctx.FileSize);
                progressed = true;
                return Task.FromResult(((byte[])[1, 2, 3], (List<uint[]>)[[7, 8, 9, 10]]));
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferProgress);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://transfer.it/t/XHaaaaaaaaaa", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.True(progressed);
    }

    [Fact]
    public async Task RunAsync_SetupFails_YieldsAttemptFailedWithoutUpload()
    {
        // xn returns -9 (ENOENT) → setup phase fails before the upload runs.
        MegaApi api = StubApi(["[\"HANDLE\"]", null!, "-9"]);

        bool uploadRan = false;
        TransferItPipeline pipeline = new(
            _ => api,
            (pool, ctx, ulKey, progress, ct) => { uploadRan = true; return Task.FromResult(((byte[])[], (List<uint[]>)[])); });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.False(uploadRan);
    }

    /// <summary>Builds a <see cref="MegaApi"/> over canned responses with fixed keys; a null entry at
    /// index 1 is replaced by a tsid valid for the injected master key (so the session ceremony's
    /// verification passes).</summary>
    private static MegaApi StubApi(string?[] responses)
    {
        uint[] masterKey = [10, 20, 30, 40];
        Queue<uint[]> keys = new([masterKey, [1, 2, 3, 4], [5, 6, 7, 8], [9, 9, 9, 9]]);

        byte[] first16 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        byte[] tail16 = MegaCrypto.A32ToBytes(MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes(masterKey), MegaCrypto.BytesToA32(first16)));
        string tsidB64 = MegaCrypto.B64UrlEncode([.. first16, .. tail16]);

        Queue<string> queue = new(responses.Select(r => r ?? $"[{{\"tsid\":\"{tsidB64}\"}}]"));
        return new MegaApi((url, body, ct) => Task.FromResult(new HttpResponseSnapshot(200, queue.Dequeue(), [])), randKey: keys.Dequeue);
    }

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
        FilePath = @"C:\nope\x.bin",
        FileName = "x.bin",
        FileSize = 100,
        HosterName = "Transfer.it",
        Credentials = new FileHosterLoginDto { FileHosterName = "Transfer.it", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
