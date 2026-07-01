// <copyright file="WormholePipelineTests.cs" company="CSUploader">
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
using CSUploader.Upload.Pipeline.Hosters.Wormhole;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Wormhole;

/// <summary>
/// Orchestration tests for <see cref="WormholePipeline"/> — the room → encrypt → manifest → auth-upload →
/// B2 → finish flow. The JSON API, the B2 blob upload, and key/salt generation are stubbed (the crypto +
/// torrent are KAT-tested elsewhere), so these lock in the event sequence, the manifest/blob wiring, the
/// share link, and the failure/retry branches.
/// </summary>
public class WormholePipelineTests
{
    // Fixed key/salt/header-salt so the link is deterministic (key 00..0f → base64url "AAECAwQFBgcICQoLDA0ODw").
    private const string ExpectedFragment = "AAECAwQFBgcICQoLDA0ODw";
    private const string RoomJson = """{"id":"TESTROOM","writerToken":"WTOK","maxCloudSize":5500000000,"maxDownloads":100,"lifetime":86400}""";
    private const string AuthJson = """[{"uploadUrl":"https://pod.mock/b2_upload_file","authorizationToken":"B2TOK"}]""";

    [Fact]
    public void Properties_DeclareWormholeConfig()
    {
        WormholePipeline pipeline = new();
        Assert.Equal("Wormhole", pipeline.Name);
        Assert.Equal(5_500_000_000L, pipeline.MaxFileSize);
        Assert.Equal(1, pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Wormhole"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_UploadsAndReturnsWormholeLinkWithKeyFragment()
    {
        using TempFile file = TempFile.OfSize(2000); // small → 1 blob
        WormholeCalls calls = new();
        WormholePipeline pipeline = MakePipeline(calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(file.Path, 2000), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferProgress);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal($"https://wormhole.app/TESTROOM#{ExpectedFragment}", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // room create sent {readerToken, salt}
        Assert.Contains("readerToken", calls.RoomBody, StringComparison.Ordinal);
        Assert.Contains("salt", calls.RoomBody, StringComparison.Ordinal);

        // manifest PATCH carried the infoHash + encryptedTorrentFile + multiFile:false, with the bearer auth
        Assert.Equal("PATCH", calls.ManifestMethod);
        Assert.Contains("\"infoHash\"", calls.ManifestBody, StringComparison.Ordinal);
        Assert.Contains("\"encryptedTorrentFile\"", calls.ManifestBody, StringComparison.Ordinal);
        Assert.Contains("\"multiFile\":false", calls.ManifestBody, StringComparison.Ordinal);
        Assert.Equal("Bearer sync-v1 WTOK", calls.ManifestAuth);

        // one B2 blob for a 2 KB file, named TESTROOM/0, sized to the ciphertext, with a real sha1 header
        Assert.Contains("\"numTokens\":1", calls.AuthBody, StringComparison.Ordinal);
        (string url, int size, string fileName, string sha1) = Assert.Single(calls.Blobs);
        Assert.Equal("https://pod.mock/b2_upload_file", url);
        Assert.Equal("TESTROOM/0", fileName);
        Assert.Equal((int)WormholeCrypto.EncryptedSize(2000), size);
        Assert.Equal(40, sha1.Length); // sha1 hex

        Assert.True(calls.Finished);
    }

    [Fact]
    public async Task RunAsync_FileExceedsCloudCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        WormholeCalls calls = new();
        WormholePipeline pipeline = MakePipeline(calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(@"C:\nope\x.bin", 5_500_000_001L), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("wormhole.app", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Null(calls.RoomBody); // no room created
    }

    [Fact]
    public async Task RunAsync_RoomCreateFails_YieldsAttemptFailedWithoutUpload()
    {
        using TempFile file = TempFile.OfSize(100);
        WormholeCalls calls = new();
        WormholePipeline pipeline = MakePipeline(calls, roomResponse: new HttpResponseSnapshot(500, "server error", []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(file.Path, 100), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls.Blobs);
    }

    [Fact]
    public async Task RunAsync_BlobRejected_YieldsAttemptFailedWithoutFinish()
    {
        using TempFile file = TempFile.OfSize(100);
        WormholeCalls calls = new();
        WormholePipeline pipeline = MakePipeline(calls, blobResult: (_, _, _, progress) => { progress(1); return new HttpResponseSnapshot(401, "bad token", []); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(file.Path, 100), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.False(calls.Finished);
    }

    [Fact]
    public async Task RunAsync_BlobTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send B2 abort must PROPAGATE (retryable) so the shared retry layer re-runs against a fresh
        // room — finish-upload never ran, so nothing was committed.
        using TempFile file = TempFile.OfSize(100);
        WormholeCalls calls = new();
        WormholePipeline pipeline = MakePipeline(calls, blobResult: (_, _, _, _) =>
            throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054)))));

        UploadBodyTransferException ex = await Assert.ThrowsAsync<UploadBodyTransferException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(file.Path, 100), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex));
        Assert.False(calls.Finished);
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

    private static WormholePipeline MakePipeline(
        WormholeCalls calls,
        HttpResponseSnapshot? roomResponse = null,
        Func<string, byte[], IReadOnlyDictionary<string, string>, Action<long>, HttpResponseSnapshot>? blobResult = null)
    {
        // Deterministic key/salt/header-salt: mainKey 00..0f (→ known fragment), salt 10..1f, headerSalt 20..2f.
        Queue<byte[]> rand = new([
            [.. Enumerable.Range(0, 16).Select(i => (byte)i)],
            [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)],
            [.. Enumerable.Range(0x20, 16).Select(i => (byte)i)],
        ]);

        return new WormholePipeline(
            sendJson: (method, url, json, headers) =>
            {
                string? auth = headers?.TryGetValue("Authorization", out string? a) == true ? a : null;
                if (url.EndsWith("/api/room", StringComparison.Ordinal))
                {
                    calls.RoomBody = json;
                    return roomResponse ?? new HttpResponseSnapshot(200, RoomJson, []);
                }

                if (url.Contains("/b2/auth-upload", StringComparison.Ordinal))
                {
                    calls.AuthBody = json;
                    return new HttpResponseSnapshot(200, AuthJson, []);
                }

                if (url.Contains("/b2/finish-upload", StringComparison.Ordinal))
                {
                    calls.Finished = true;
                    return new HttpResponseSnapshot(200, """{"success":true}""", []);
                }

                // the manifest PATCH /api/room/<id>
                calls.ManifestMethod = method.Method;
                calls.ManifestBody = json ?? string.Empty;
                calls.ManifestAuth = auth;
                return new HttpResponseSnapshot(200, json ?? "{}", []);
            },
            uploadBlob: blobResult ?? ((url, blob, headers, progress) =>
            {
                progress(blob.Length);
                calls.Blobs.Add((url, blob.Length, headers["X-Bz-File-Name"], headers["X-Bz-Content-Sha1"]));
                return new HttpResponseSnapshot(200, """{"contentSha1":"ok"}""", []);
            }),
            randBytes: _ => rand.Dequeue());
    }

    private sealed class WormholeCalls
    {
        public string? RoomBody { get; set; }

        public string? AuthBody { get; set; }

        public string? ManifestMethod { get; set; }

        public string ManifestBody { get; set; } = string.Empty;

        public string? ManifestAuth { get; set; }

        public List<(string Url, int Size, string FileName, string Sha1)> Blobs { get; } = [];

        public bool Finished { get; set; }
    }

    private static AttemptContext MakeContext(string filePath, long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = filePath,
        FileName = "clip.bin",
        FileSize = fileSize,
        HosterName = "Wormhole",
        Credentials = new FileHosterLoginDto { FileHosterName = "Wormhole", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed class TempFile : IDisposable
    {
        public string Path { get; private init; } = string.Empty;

        public static TempFile OfSize(int bytes)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wh-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "clip.bin");
            byte[] data = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                data[i] = (byte)((i * 3) + 1);
            }

            File.WriteAllBytes(path, data);
            return new TempFile { Path = path };
        }

        public void Dispose()
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (dir is not null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
