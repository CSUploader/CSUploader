// <copyright file="FilebinPipelineTests.cs" company="CSUploader">
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
/// Filebin — one POST per file, and the only host here with a published OpenAPI spec. Fixtures are its
/// real 201 envelope and its documented failures (2026-08-08, verified by uploading). What matters most
/// is pinned first: a bin is a PUBLIC namespace, so every upload must get its own unguessable one.
/// </summary>
public class FilebinPipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string CreatedJson = """
        {"bin":{"id":"3cd60eff764599b166ce735939","readonly":false,"bytes":786432,"files":1,"expired_at":"2026-08-15T06:50:28.145651Z"},"file":{"filename":"probe.rar","content-type":"application/octet-stream","bytes":786432,"md5":"llhSOOfqsjw3NIHY","sha256":"f5765ba8f4eae6a4"}}
        """;

    public FilebinPipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_GivesEveryFileItsOwnUnguessableBin()
    {
        // A bin has no password or token: whoever knows its name sees everything in it. So two files
        // must never share one (a single link would hand over the rest of the package), and the name
        // must not be guessable (or the contents are public to anyone at all).
        List<string> endpoints = [];
        FilebinPipeline pipeline = new((_, endpoint, _, _) =>
        {
            endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseSnapshot(201, CreatedJson, Array.Empty<string>()));
        });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        string[] bins = [.. endpoints.Select(e => new Uri(e).AbsolutePath.Split('/')[1])];
        Assert.Equal(2, bins.Length);
        Assert.NotEqual(bins[0], bins[1]);
        Assert.All(bins, b => Assert.Matches("^[0-9a-f]{26}$", b));
    }

    [Fact]
    public async Task RunAsync_LinksTheNameTheServerStored_NotTheOneWeSent()
    {
        // Filebin sanitises what it must. A link built from our filename would 404 whenever the two
        // differ, and the difference wouldn't show up until someone clicked it.
        const string Renamed = """{"bin":{"id":"b"},"file":{"filename":"probe_renamed.rar","bytes":4096}}""";
        FilebinPipeline pipeline = new((_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(201, Renamed, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        string url = Assert.Single(events.OfType<TransferCompleted>()).FileUrl;
        Assert.EndsWith("/probe_renamed.rar", url, StringComparison.Ordinal);
        Assert.StartsWith("https://filebin.net/", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SendsTheChecksumOnlyWhenTheAppAlreadyHasOne()
    {
        // The API verifies Content-MD5 and answers 400 on a mismatch, which is worth having — but
        // hashing costs a full extra read, so it is used when the hash exists rather than forced.
        List<IReadOnlyDictionary<string, string>?> sent = [];
        FilebinPipeline pipeline = new((_, _, headers, _) =>
        {
            sent.Add(headers);
            return Task.FromResult(new HttpResponseSnapshot(201, CreatedJson, Array.Empty<string>()));
        });

        // The scheduler's hash is MD5 in HEX; the API wants base64.
        await DrainAsync(pipeline.RunAsync(MakeContext() with { FileHash = "5d41402abc4b2a76b9719d911017c592" }, CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("XUFAKrxLKna5cZ2REBfFkg==", sent[0]!["Content-MD5"]);
        Assert.False(sent[1]!.ContainsKey("Content-MD5"));
    }

    [Theory]
    [InlineData("5d41402abc4b2a76b9719d911017c592", "XUFAKrxLKna5cZ2REBfFkg==")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-a-hash", null)]                      // wrong length
    [InlineData("zz41402abc4b2a76b9719d911017c592", null)] // right length, not hex
    public void ToBase64Md5_ConvertsOnlyARealDigest(string? hex, string? expected)
    {
        // A malformed value would fail the upload for a reason that has nothing to do with the file,
        // which is a worse outcome than simply not verifying.
        Assert.Equal(expected, FilebinPipeline.ToBase64Md5(hex));
    }

    [Theory]
    [InlineData(400, "checksum")]
    [InlineData(403, "file type")]
    [InlineData(405, "expired")]
    [InlineData(411, "Content-Length")]
    [InlineData(503, "retry later")]
    public void ParseUploadResponse_ExplainsTheApisOwnFailures(int status, string expected)
    {
        // Each of these is a documented response in filebin's OpenAPI spec. Naming them beats handing
        // the user a bare status code for a service whose failures are all actionable.
        (string? name, string? _, string? error) =
            FilebinPipeline.ParseUploadResponse(new HttpResponseSnapshot(status, "nope", Array.Empty<string>()));

        Assert.Null(name);
        Assert.Contains(expected, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUploadResponse_A201ThatNamesNoFile_IsAFailure()
    {
        // Success-shaped failure: a 201 whose envelope has no file is not an upload, and treating it
        // as one would hand back a link to nothing.
        (string? name, string? _, string? error) =
            FilebinPipeline.ParseUploadResponse(new HttpResponseSnapshot(201, """{"bin":{"id":"b"}}""", Array.Empty<string>()));

        Assert.Null(name);
        Assert.Contains("named no file", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filebin_IsAnonymousOnly_WithNoAccountsAndNoStatedCap()
    {
        FilebinPipeline pipeline = new();
        Assert.Equal("Filebin", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.SupportsAccounts);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Equal("filebin.net", FileHosterClient.FileHosters["Filebin"]);

        // Hashing is deliberately not forced — see the pipeline's remarks on the cost of an extra read.
        Assert.False(pipeline.RequiresHashingBeforeUpload);
    }

    private AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = 4096,
        HosterName = "Filebin",
        Credentials = new FileHosterLoginDto { FileHosterName = "Filebin", IsAnonymous = true },
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
