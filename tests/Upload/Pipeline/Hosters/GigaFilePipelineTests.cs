// <copyright file="GigaFilePipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
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
/// GigaFile — chunked multipart to a rotating node. Fixtures are the real homepage globals and the
/// real chunk replies (2026-08-07, verified by uploading). What's pinned above all is the
/// <c>lifetime</c> field: their page ships 7 days where the service allows 100, so a client that
/// copies the default silently throws away 93 days.
/// </summary>
public class GigaFilePipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");

    private const string HomeJs = """
        <html><head><script>
        var server = "115.gigafile.nu";
        var chunk_size = "100mb";
        var max_size = "300gb";
        </script></head><body></body></html>
        """;

    private const string DoneJson = """
        {"status":0,"url":"https://115.gigafile.nu/1115-b681ed7e0cfce3b299ba6c161aa70ba95","delkey":"4abb","filename":"1115-b681ed7e0cfce3b299ba6c161aa70ba95"}
        """;

    public GigaFilePipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_SendsTheMaximumLifetime_NotThePagesDefault()
    {
        // Their slider offers 3/5/7/14/30/60/100 and the page ships 7. Sending the default (or omitting
        // the field) would keep a release for a week instead of over three months — the same trap
        // tmpfiles.org (1 hour), qu.ax (30 days) and Litterbox (1 hour) each set.
        List<IReadOnlyDictionary<string, string>> chunks = [];
        GigaFilePipeline pipeline = new(
            getOverride: _ => Task.FromResult(HomeJs),
            chunkOverride: (_, fields, _, _) =>
            {
                chunks.Add(new Dictionary<string, string>(fields));
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("100", Assert.Single(chunks)["lifetime"]);
    }

    [Fact]
    public async Task RunAsync_PostsToTheNodeTheHomepageNames_AndReturnsTheLastChunksUrl()
    {
        List<string> endpoints = [];
        GigaFilePipeline pipeline = new(
            getOverride: _ => Task.FromResult(HomeJs),
            chunkOverride: (url, _, _, _) =>
            {
                endpoints.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // The node ROTATES, so it must come from the page rather than a constant.
        Assert.Equal("https://115.gigafile.nu/upload_chunk.php", Assert.Single(endpoints));

        // The share URL lives on the node's host, not the apex — copying it from the apex would 404.
        Assert.Equal(
            "https://115.gigafile.nu/1115-b681ed7e0cfce3b299ba6c161aa70ba95",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_SingleChunkFile_IsStillChunkZeroOfOne()
    {
        List<IReadOnlyDictionary<string, string>> chunks = [];
        GigaFilePipeline pipeline = new(
            getOverride: _ => Task.FromResult(HomeJs),
            chunkOverride: (_, fields, _, _) =>
            {
                chunks.Add(new Dictionary<string, string>(fields));
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        IReadOnlyDictionary<string, string> only = Assert.Single(chunks);
        Assert.Equal("0", only["chunk"]);   // 0-based, as their uploader sends
        Assert.Equal("1", only["chunks"]);  // a sub-chunk-size file is still one chunk, never zero
        Assert.Equal("probe.bin", only["name"]);
        Assert.Equal(32, only["id"].Length); // client-generated; the server has never seen it before
    }

    [Fact]
    public async Task RunAsync_MultiChunk_ReplaysTheSessionCookiesTheFirstChunkIssued()
    {
        // THE BUG A SINGLE-CHUNK UPLOAD CANNOT SHOW. The first chunk answers with gfsid (the upload
        // session, which is what knows the destination directory) and Apache (sticky routing to the
        // backend holding the partial file). This app's handler keeps no cookies, so without replaying
        // them by hand the host fails the LAST chunk — after the whole file has transferred — with
        // "couldn't get the destination directory; cookies may be disabled". Found by uploading a
        // two-chunk file for real; the six-megabyte one passed happily.
        List<IReadOnlyDictionary<string, string>?> headers = [];
        List<string> ids = [];
        int seen = 0;
        GigaFilePipeline pipeline = new(
            getOverride: _ => Task.FromResult(HomeJs),
            chunkOverride: (_, fields, sent, _) =>
            {
                headers.Add(sent is null ? null : new Dictionary<string, string>(sent));
                ids.Add(fields["id"]);
                seen++;

                // Chunk 1 issues the session; chunk 2 completes the upload.
                return Task.FromResult(seen == 1
                    ? new HttpResponseSnapshot(200, """{"status":0}""", ["gfsid=abc123; path=/", "Apache=10.0.0.1.1; path=/"])
                    : new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        // 100 MiB + a bit, so the real chunk arithmetic produces exactly two chunks.
        AttemptContext ctx = MakeContext() with { FileSize = (100L * 1024 * 1024) + 4096 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, headers.Count);

        // The first chunk has no session to send yet…
        Assert.False(headers[0]!.ContainsKey("Cookie"));

        // …and the second must send back BOTH cookies, or the upload dies at the finish line.
        string cookie = Assert.Contains("Cookie", (IDictionary<string, string>)headers[1]!);
        Assert.Contains("gfsid=abc123", cookie, StringComparison.Ordinal);
        Assert.Contains("Apache=10.0.0.1.1", cookie, StringComparison.Ordinal);

        // One id ties the chunks together — a fresh id per chunk would orphan the partial file.
        Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task RunAsync_HomepageWithoutANode_FailsWithoutSendingAnything()
    {
        bool sent = false;
        GigaFilePipeline pipeline = new(
            getOverride: _ => Task.FromResult("<html>maintenance</html>"),
            chunkOverride: (_, _, _, _) =>
            {
                sent = true;
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("upload node", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(sent);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    // status 0 is SUCCESS here — the inverse of most hosts, and reading it the usual way would treat
    // every good chunk as a failure.
    [InlineData("""{"status":0}""", null, null)]
    [InlineData("""{"status":0,"url":"https://115.gigafile.nu/abc","delkey":"9z"}""", "https://115.gigafile.nu/abc", "9z")]
    [InlineData("""{"status":"0"}""", null, null)]
    public void ParseChunkResponse_ReadsTheHostsOwnSuccessFlag(string body, string? url, string? delkey)
    {
        (string? gotUrl, string? gotKey, string? error) =
            GigaFilePipeline.ParseChunkResponse(new HttpResponseSnapshot(200, body, Array.Empty<string>()), 0, 1);

        Assert.Null(error);
        Assert.Equal(url, gotUrl);
        Assert.Equal(delkey, gotKey);
    }

    [Theory]
    [InlineData("""{"status":1,"message":"file size over"}""", "file size over")]
    [InlineData("""{"status":9}""", "refused")]
    [InlineData("<html>502</html>", "wasn't JSON")]
    [InlineData("""{"ok":true}""", "no status")]
    public void ParseChunkResponse_SurfacesTheHostsOwnRefusal(string body, string expected)
    {
        (string? url, string? _, string? error) =
            GigaFilePipeline.ParseChunkResponse(new HttpResponseSnapshot(200, body, Array.Empty<string>()), 0, 3);

        Assert.Null(url);
        Assert.Contains(expected, error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1/3", error!, StringComparison.Ordinal); // which chunk, for a long upload
    }

    [Fact]
    public async Task RunAsync_OversizedFile_RejectedBeforeAnyHttp()
    {
        bool touched = false;
        GigaFilePipeline pipeline = new(
            getOverride: _ => { touched = true; return Task.FromResult(HomeJs); },
            chunkOverride: (_, _, _, _) => { touched = true; return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext() with { FileSize = (300L * 1024 * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.False(touched);
    }

    [Fact]
    public void GigaFile_IsAnonymousOnly_WithTheLargestCapHere()
    {
        GigaFilePipeline pipeline = new();
        Assert.Equal("GigaFile", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(300L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal("gigafile.nu", FileHosterClient.FileHosters["GigaFile"]);

        // No accounts exist on this service, so the credential dialog must not offer a sign-in.
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("GigaFile"));
    }

    private AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.bin",
        FileSize = 4096,
        HosterName = "GigaFile",
        Credentials = new FileHosterLoginDto { FileHosterName = "GigaFile", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }
}
