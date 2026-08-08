// <copyright file="UploadNowPipelineTests.cs" company="CSUploader">
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
/// UploadNow — Firebase anonymous identity, the host's own SigV4 signer, and an R2 multipart. Fixtures
/// are the real bodies from a capture (2026-08-08), verified by uploading. What's pinned is the order
/// of the four stages, that the signature this app asks for is a real SigV4 string-to-sign, and that
/// the link is the FOLDER's — the one thing a plausible guess would get wrong.
/// </summary>
public class UploadNowPipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string SignUpJson = """{"kind":"identitytoolkit#SignupNewUserResponse","idToken":"tok-abc","refreshToken":"r","expiresIn":"3600","localId":"OWNER123"}""";
    private const string FolderJson = """{"id":"Hzg2ZNZ"}""";
    private const string DeclareJson = """
        {"ids":["b483010d-76bd-4a01-9574-3447237bbd5b"],"bucketConfig":{"signerUrl":"/signer/buckets/43057deb/sign-url","aws_key":"2f488bd324502ec2","awsSignatureVersion":"4","bucket":"upnow-prod","cloudfront":false,"computeContentMd5":true,"awsRegion":"auto","aws_url":"https://acct.r2.cloudflarestorage.com/upnow-prod","maxConcurrentParts":5}}
        """;
    private const string InitiateXml = """<?xml version="1.0" encoding="UTF-8"?><InitiateMultipartUploadResult><UploadId>UP-1</UploadId></InitiateMultipartUploadResult>""";
    private const string CompleteXml = """<?xml version="1.0" encoding="UTF-8"?><CompleteMultipartUploadResult><ETag>&quot;abc-1&quot;</ETag></CompleteMultipartUploadResult>""";

    public UploadNowPipelineTests() => File.WriteAllBytes(_file, new byte[2048]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_WalksTheFourStages_AndReturnsTheFoldersLink()
    {
        List<string> calls = [];
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                calls.Add($"{method} {Trim(url)}");
                return Task.FromResult(Reply(url, body, headers));
            },
            partOverride: (url, _, _, headers) =>
            {
                calls.Add("PUT part");
                Assert.StartsWith("AWS4-HMAC-SHA256 Credential=2f488bd324502ec2/", headers["Authorization"], StringComparison.Ordinal);
                Assert.Equal("UNSIGNED-PAYLOAD", headers["x-amz-content-sha256"]);
                Assert.True(headers.ContainsKey("Content-MD5")); // signed, so it must be present
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag-1\""));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());

        // The link is the FOLDER's, not the file's — the file id would render an empty page.
        Assert.Equal("https://uploadnow.io/f/Hzg2ZNZ", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        Assert.Equal(
            [
                "POST accounts:signUp",     // a Firebase ANONYMOUS identity first
                "POST /api/file/folders",   // …then a folder, because the link is the folder's
                "POST /api/file/files",     // …then the file is declared, which yields the bucket config
                "GET sign-url",             // initiate: signed
                "POST ?uploads",
                "GET sign-url",             // the part: signed
                "PUT part",
                "GET sign-url",             // complete: signed
                "POST ?uploadId",
                "PUT upload-done",          // and the site is told, or the file stays invisible
            ],
            calls);
    }

    [Fact]
    public async Task RunAsync_AsksTheSignerForARealStringToSign()
    {
        // The signer only returns a signature — this app builds the canonical request and string-to-sign
        // itself. If that string stops being a SigV4 one, R2 rejects every request with a signature
        // mismatch, so its shape is worth pinning here rather than discovering in the field.
        List<string> toSign = [];
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                if (url.Contains("sign-url", StringComparison.Ordinal))
                {
                    string encoded = url.Split("to_sign=")[1].Split('&')[0];
                    toSign.Add(Uri.UnescapeDataString(encoded));
                }

                return Task.FromResult(Reply(url, body, headers));
            },
            partOverride: (_, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag-1\"")));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        string[] lines = Assert.IsType<string>(toSign[0]).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("AWS4-HMAC-SHA256", lines[0]);
        Assert.Matches(@"^\d{8}T\d{6}Z$", lines[1]);
        Assert.Matches(@"^\d{8}/auto/s3/aws4_request$", lines[2]);
        Assert.Matches("^[0-9a-f]{64}$", lines[3]);   // the canonical request's SHA-256

        // Every R2 call is signed separately — initiate, the part, complete.
        Assert.Equal(3, toSign.Count);
    }

    [Fact]
    public async Task RunAsync_OneIdentityIsMintedForTheWholeBatch()
    {
        // Creating a guest per file is how gofile's per-IP limit gets tripped; one per batch is the
        // site's own behaviour too.
        int signUps = 0;
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                if (url.Contains("signUp", StringComparison.Ordinal))
                {
                    signUps++;
                }

                return Task.FromResult(Reply(url, body, headers));
            },
            partOverride: (_, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag-1\"")));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(1, signUps);
    }

    [Fact]
    public async Task RunAsync_WhenTheStorageWontAssembleTheFile_ReportsIt()
    {
        // R2 can report a failure INSIDE a 200 on the complete call, which is exactly the
        // success-shaped failure that has bitten other hosts here.
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) => Task.FromResult(
                url.Contains("uploadId=", StringComparison.Ordinal) && !url.Contains("partNumber", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(200, "<Error><Code>InvalidPart</Code></Error>", Array.Empty<string>())
                    : Reply(url, body, headers)),
            partOverride: (_, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag-1\"")));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("assemble", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_APartWithoutAnETag_FailsRatherThanCompletingABrokenFile()
    {
        // Completing a multipart upload needs every part's ETag. Sending the complete call without one
        // would produce a file the host assembles wrongly or refuses — better to stop here and say why.
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) => Task.FromResult(Reply(url, body, headers)),
            partOverride: (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("ETag", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeclareJson, true)]
    [InlineData("""{"ids":[],"bucketConfig":{}}""", false)]
    [InlineData("""{"ids":["x"]}""", false)]
    [InlineData("<html>502</html>", false)]
    public void ParseDeclaredFile_NeedsBothTheIdAndTheBucketConfig(string body, bool ok)
    {
        (string? id, UploadNowPipeline.BucketConfig? cfg, string? error) = UploadNowPipeline.ParseDeclaredFile(body);

        Assert.Equal(ok, id is not null && cfg is not null);
        Assert.Equal(ok, error is null);
        if (ok)
        {
            Assert.Equal("https://acct.r2.cloudflarestorage.com/upnow-prod", cfg!.Value.AwsUrl);
            Assert.Equal("auto", cfg.Value.Region);
        }
    }

    [Fact]
    public void UploadNow_IsAnonymousOnly_AndOffersNoAccounts()
    {
        UploadNowPipeline pipeline = new();
        Assert.Equal("UploadNow", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Its accounts are all paid, so there is no free credential to add — the dialog leaves it out.
        Assert.False(pipeline.SupportsAccounts);
        Assert.Equal(100L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal("uploadnow.io", FileHosterClient.FileHosters["UploadNow"]);
    }

    /// <summary>Answers each stage with the shape the live host uses.</summary>
    private static HttpResponseSnapshot Reply(string url, string? body, IReadOnlyDictionary<string, string>? headers)
    {
        _ = body;
        _ = headers;
        if (url.Contains("signUp", StringComparison.Ordinal)) return new HttpResponseSnapshot(200, SignUpJson, Array.Empty<string>());
        if (url.EndsWith("/api/file/folders", StringComparison.Ordinal)) return new HttpResponseSnapshot(201, FolderJson, Array.Empty<string>());
        if (url.EndsWith("/api/file/files", StringComparison.Ordinal)) return new HttpResponseSnapshot(201, DeclareJson, Array.Empty<string>());
        if (url.Contains("sign-url", StringComparison.Ordinal)) return new HttpResponseSnapshot(200, "deadbeef", Array.Empty<string>());
        if (url.EndsWith("?uploads", StringComparison.Ordinal)) return new HttpResponseSnapshot(200, InitiateXml, Array.Empty<string>());
        if (url.Contains("upload-done", StringComparison.Ordinal)) return new HttpResponseSnapshot(200, """{"message":"OK"}""", Array.Empty<string>());
        return new HttpResponseSnapshot(200, CompleteXml, Array.Empty<string>());
    }

    private static string Trim(string url)
    {
        if (url.Contains("signUp", StringComparison.Ordinal)) return "accounts:signUp";
        if (url.Contains("sign-url", StringComparison.Ordinal)) return "sign-url";
        if (url.Contains("upload-done", StringComparison.Ordinal)) return "upload-done";
        if (url.EndsWith("?uploads", StringComparison.Ordinal)) return "?uploads";
        if (url.Contains("uploadId=", StringComparison.Ordinal)) return "?uploadId";
        return url.Replace("https://uploadnow.io", string.Empty, StringComparison.Ordinal);
    }

    private AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = 2048,
        HosterName = "UploadNow",
        Credentials = new FileHosterLoginDto { FileHosterName = "UploadNow", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
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
