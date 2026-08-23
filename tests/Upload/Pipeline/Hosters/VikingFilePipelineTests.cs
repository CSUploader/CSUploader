// <copyright file="VikingFilePipelineTests.cs" company="CSUploader">
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
/// VikingFile's documented API path: get-upload-url → presigned R2 part PUTs (keep each ETag) →
/// complete-upload. Fixtures mirror the live responses captured 2026-07-30, including the two shapes
/// that would silently break a naive parser: <c>partSize</c> differs 10× from the published docs, and
/// <c>size</c> comes back as a JSON STRING.
/// </summary>
public class VikingFilePipelineTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (string path in _tempFiles)
        {
            File.Delete(path);
        }

        GC.SuppressFinalize(this);
    }

    private const string PartUrl1 = "https://asia-upload.abc123.r2.cloudflarestorage.com/nwZY81TkzI?uploadId=UP1&partNumber=1&X-Amz-Signature=sig1";
    private const string PartUrl2 = "https://asia-upload.abc123.r2.cloudflarestorage.com/nwZY81TkzI?uploadId=UP1&partNumber=2&X-Amz-Signature=sig2";

    // Live shape. NOTE partSize=104857600 (100 MiB) — the published docs say 1073741824 (1 GiB), so
    // this value must come from the response or every multi-part upload is mis-sliced 10x over.
    private static string InitJson(int parts, long partSize = 104857600) => parts == 1
        ? $$"""{"uploadId":"UP1","key":"nwZY81TkzI","partSize":{{partSize}},"numberParts":1,"urls":["{{PartUrl1}}"]}"""
        : $$"""{"uploadId":"UP1","key":"nwZY81TkzI","partSize":{{partSize}},"numberParts":2,"urls":["{{PartUrl1}}","{{PartUrl2}}"]}""";

    // Live shape — "size" is a STRING, not a number.
    private const string CompleteJson =
        """{"name":"vf_probe.bin","size":"4096","hash":"XvQ7ooF9oK","url":"https:\/\/vikingfile.com\/f\/XvQ7ooF9oK"}""";

    [Fact]
    public async Task RunAsync_SinglePart_InitiatesWithTheSize_PutsThePart_ThenFinalises()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Form)> posts = [];
        List<string> puts = [];

        VikingFilePipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                posts.Add((url, new Dictionary<string, string>(form, StringComparer.Ordinal)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, url.Contains("complete-upload", StringComparison.Ordinal) ? CompleteJson : InitJson(1), Array.Empty<string>()));
            },
            putPartOverride: (url, part, offset, length, body, report, ct) =>
            {
                puts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag{part}"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("https://vikingfile.com/f/XvQ7ooF9oK", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        Assert.Equal(PartUrl1, Assert.Single(puts));
        Assert.Equal(2, posts.Count);

        // get-upload-url is told the size in bytes — that's what sizes the parts.
        Assert.EndsWith("/api/get-upload-url", posts[0].Url, StringComparison.Ordinal);
        Assert.Equal("4096", posts[0].Form["size"]);

        // complete-upload carries the handle, the filename, an EMPTY user (anonymous) and the ETags.
        Assert.EndsWith("/api/complete-upload", posts[1].Url, StringComparison.Ordinal);
        IReadOnlyDictionary<string, string> done = posts[1].Form;
        Assert.Equal("nwZY81TkzI", done["key"]);
        Assert.Equal("UP1", done["uploadId"]);
        Assert.Equal("1mb.bin", done["name"]);
        Assert.Equal(string.Empty, done["user"]);
        Assert.Equal("1", done["parts[0][PartNumber]"]);
        Assert.Equal("etag1", done["parts[0][ETag]"]);
    }

    [Fact]
    public async Task RunAsync_MultiPart_PutsEveryPartInOrder_AndReportsAllETags()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Form)> posts = [];
        List<string> puts = [];

        VikingFilePipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                posts.Add((url, new Dictionary<string, string>(form, StringComparer.Ordinal)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, url.Contains("complete-upload", StringComparison.Ordinal) ? CompleteJson : InitJson(2, partSize: 4096), Array.Empty<string>()));
            },
            putPartOverride: (url, part, offset, length, body, report, ct) =>
            {
                puts.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: $"etag{part}"));
            });

        // 150 MiB over a 100 MiB part size → two parts.
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(8192), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal([PartUrl1, PartUrl2], puts);

        IReadOnlyDictionary<string, string> done = posts[1].Form;
        Assert.Equal("1", done["parts[0][PartNumber]"]);
        Assert.Equal("etag1", done["parts[0][ETag]"]);
        Assert.Equal("2", done["parts[1][PartNumber]"]);
        Assert.Equal("etag2", done["parts[1][ETag]"]);
    }

    [Fact]
    public async Task RunAsync_PartWithoutAnETag_FailsWithoutFinalising()
    {
        // complete-upload cannot finalise an R2 multipart without every part's ETag, so a missing one
        // must stop the attempt rather than send a finalise that's guaranteed to fail.
        List<string> postedUrls = [];
        VikingFilePipeline pipeline = new(
            postFormOverride: (url, _) =>
            {
                postedUrls.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, InitJson(1), Array.Empty<string>()));
            },
            putPartOverride: (_, _, _, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Contains("no ETag", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(postedUrls); // get-upload-url only — never complete-upload
    }

    [Fact]
    public async Task RunAsync_PartRejected_SurfacesTheStatus_AndDoesNotFinalise()
    {
        List<string> postedUrls = [];
        VikingFilePipeline pipeline = new(
            postFormOverride: (url, _) =>
            {
                postedUrls.Add(url);
                return Task.FromResult(new HttpResponseSnapshot(200, InitJson(1), Array.Empty<string>()));
            },
            putPartOverride: (_, _, _, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(403, "<Error>AccessDenied</Error>", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("403", fail.Reason, StringComparison.Ordinal);
        Assert.Single(postedUrls);
    }

    [Fact]
    public async Task RunAsync_InitiationRefused_FailsBeforeSendingAnyBytes()
    {
        List<string> puts = [];
        VikingFilePipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(500, "server error", Array.Empty<string>())),
            putPartOverride: (url, _, _, _, _, _, _) => { puts.Add(url); return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), ETag: "e")); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(puts);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    // The live shape, and the doc's 1 GiB partSize — both must round-trip whatever the value.
    [InlineData("""{"uploadId":"U","key":"K","partSize":104857600,"numberParts":1,"urls":["https://x/1"]}""", 104857600L, 1)]
    [InlineData("""{"uploadId":"U","key":"K","partSize":1073741824,"numberParts":3,"urls":["https://x/1","https://x/2","https://x/3"]}""", 1073741824L, 3)]
    [InlineData("""{"uploadId":"U","key":"K","partSize":"104857600","numberParts":1,"urls":["https://x/1"]}""", 104857600L, 1)] // string-typed number
    public void TryReadUploadInit_ParsesTheHandleAndParts(string json, long partSize, int parts)
    {
        var init = VikingFilePipeline.TryReadUploadInit(json);
        Assert.NotNull(init);
        Assert.Equal(partSize, init!.PartSize);
        Assert.Equal(parts, init.PartUrls.Count);
    }

    [Theory]
    [InlineData("""{"uploadId":"U","key":"K","partSize":100,"numberParts":0,"urls":[]}""")]   // zero parts would "succeed" sending nothing
    [InlineData("""{"uploadId":"U","key":"K","partSize":0,"urls":["https://x/1"]}""")]        // unusable part size
    [InlineData("""{"key":"K","partSize":100,"urls":["https://x/1"]}""")]                      // no uploadId
    [InlineData("""{"error":"quota exceeded"}""")]
    [InlineData("<html>nope</html>")]
    public void TryReadUploadInit_RefusesAnythingUnusable(string json)
        => Assert.Null(VikingFilePipeline.TryReadUploadInit(json));

    [Theory]
    [InlineData(CompleteJson, "https://vikingfile.com/f/XvQ7ooF9oK")]
    [InlineData("""{"hash":"ABC1234567"}""", "https://vikingfile.com/f/ABC1234567")] // built from hash when url is absent
    [InlineData("""{"name":"x","size":"1"}""", null)]
    [InlineData("nonsense", null)]
    public void TryReadCompletedUrl_PrefersTheServersUrl_ThenTheHash(string json, string? expected)
        => Assert.Equal(expected, VikingFilePipeline.TryReadCompletedUrl(json));

    [Fact]
    public void Properties_DeclareAnonymousVikingFile_AndItIsRegistered()
    {
        VikingFilePipeline pipeline = new();
        Assert.Equal("VikingFile", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Null(pipeline.MaxFileSize); // "Unlimited filesize"; the server sizes the parts
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("VikingFile"));
        Assert.Equal("vikingfile.com", FileHosterClient.FileHosters["VikingFile"]);

        // Anonymous-only for now: no account entry, so the editor keeps the plain U/P mode.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("VikingFile"));
    }

    [Fact]
    public async Task CheckAccountAsync_SaysAccountsArentSupportedYet()
    {
        VikingFilePipeline pipeline = new();
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u", "p", null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Anonymous", result.Message, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Writes a REAL patterned file. The pipeline now opens a FileSliceReader unconditionally — the
    /// seam receives the actual slice, which is what lets a test verify each part reads its own
    /// bytes rather than merely that the pipeline computed an offset.
    /// </summary>
    private AttemptContext MakeContext(long fileSize)
    {
        string path = Path.Combine(Path.GetTempPath(), $"csu-vf-{Guid.NewGuid():N}.bin");
        byte[] content = new byte[fileSize];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return MakeContextForPath(path, fileSize);
    }

    private static AttemptContext MakeContextForPath(string path, long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = path,
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "VikingFile",
        Credentials = new FileHosterLoginDto { FileHosterName = "VikingFile" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };
}
