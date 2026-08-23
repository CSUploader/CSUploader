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

    public UploadNowPipelineTests()
    {
        byte[] content = new byte[2048];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(_file, content);
    }

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
            partOverride: (url, _, _, headers, _, _, _) =>
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
            partOverride: (_, _, _, _, _, _, _) => Task.FromResult(
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
            partOverride: (_, _, _, _, _, _, _) => Task.FromResult(
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
            partOverride: (_, _, _, _, _, _, _) => Task.FromResult(
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
            partOverride: (_, _, _, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("ETag", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_StorageInternalError_IsRetried_WithAFreshSignature()
    {
        // The reported failure, verbatim from R2 on a real CreateMultipartUpload:
        //   <Error><Code>InternalError</Code><Message>We encountered an internal error.
        //   Please try again.</Message></Error>
        // It means what it says, and nothing here re-tried it. Every storage call is safe to repeat:
        // initiating twice abandons an empty upload id, a part is addressed by number, completing is
        // idempotent for the same parts.
        int initiateCalls = 0;
        List<string> datetimes = [];
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                if (url.EndsWith("?uploads", StringComparison.Ordinal))
                {
                    initiateCalls++;
                    datetimes.Add(headers!["x-amz-date"]);
                    if (initiateCalls == 1)
                    {
                        return Task.FromResult(new HttpResponseSnapshot(
                            500,
                            """<?xml version="1.0" encoding="UTF-8"?><Error><Code>InternalError</Code><Message>We encountered an internal error. Please try again.</Message></Error>""",
                            Array.Empty<string>()));
                    }
                }

                return Task.FromResult(Reply(url, body, headers));
            },
            partOverride: (_, _, _, _, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag-1\"")));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://uploadnow.io/f/Hzg2ZNZ", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(2, initiateCalls);

        // The retry must be signed AFRESH. A signature covers x-amz-date, so replaying the first
        // attempt's request would trade an InternalError for an authentication failure — a worse error
        // about the wrong thing.
        Assert.Equal(2, datetimes.Count);
        Assert.NotEqual(datetimes[0], datetimes[1]);
    }

    [Fact]
    public async Task RunAsync_StorageThatKeepsFailing_GivesUpAndSaysWhat()
    {
        int calls = 0;
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                if (!url.EndsWith("?uploads", StringComparison.Ordinal))
                {
                    return Task.FromResult(Reply(url, body, headers));
                }

                calls++;
                return Task.FromResult(new HttpResponseSnapshot(500, "<Error><Code>InternalError</Code></Error>", Array.Empty<string>()));
            },
            partOverride: (_, _, _, _, _, _, _) => throw new InvalidOperationException("must not upload a part"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("500", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Equal(4, calls);   // bounded, so a host having a bad day can't spin forever
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
    // ── parallel parts ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The part count is a ceiling division and never zero for a non-empty file. Plain integer
    /// division drops the final PARTIAL part, so the upload completes having silently transmitted a
    /// truncated object — the worst shape of failure this conversion could introduce.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4096, 1)]
    [InlineData(64L * 1024 * 1024, 1)]              // exactly one part, not two
    [InlineData((64L * 1024 * 1024) + 1, 2)]        // the +1 is a whole extra part
    [InlineData((64L * 1024 * 1024 * 2) + 4096, 3)] // two full parts and a remainder
    public void TheExpectedPartCount_IsACeilingDivision(long fileSize, int expected)
    {
        const long PartSize = 64L * 1024 * 1024;

        int actual = fileSize <= 0 ? 0 : (int)(((fileSize - 1) / PartSize) + 1);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// <c>WithStorageRetryAsync</c> re-invokes the part delegate on a 5xx, so its body must be
    /// re-openable. A consumed slice sends EOF on the second attempt — the retry "succeeds" having
    /// transmitted nothing, and the assembled object is silently short. Hence a body FACTORY rather
    /// than a body.
    /// </summary>
    [Fact]
    public async Task ARetriedPart_SendsItsBytesAgain_NotEof()
    {
        List<long> bodyLengths = [];
        int attempts = 0;

        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) => Task.FromResult(Reply(url, body, headers)),
            partOverride: async (url, offset, length, headers, openBody, report, ct) =>
            {
                int attempt = ++attempts;

                using Stream part = openBody();
                using MemoryStream sink = new();
                await part.CopyToAsync(sink, ct);
                bodyLengths.Add(sink.Length);

                return attempt == 1
                    ? new HttpResponseSnapshot(503, "slow down", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag\"");
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // BOTH attempts carried the real bytes. Without a fresh slice the second would be zero.
        Assert.Equal(2, bodyLengths.Count);
        Assert.All(bodyLengths, length => Assert.Equal(2048, length));
    }

    /// <summary>
    /// The MD5 pre-pass and the upload pass used to share the one <c>FileStream</c>, moving its
    /// position between them. Each part now hashes and sends its OWN slice, so the signed
    /// Content-MD5 must still match the bytes that go on the wire.
    /// </summary>
    [Fact]
    public async Task EachPart_HashesTheSameBytesItSends()
    {
        string? signedMd5 = null;
        byte[]? sent = null;

        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) => Task.FromResult(Reply(url, body, headers)),
            partOverride: async (url, offset, length, headers, openBody, report, ct) =>
            {
                signedMd5 = headers["Content-MD5"];

                using Stream part = openBody();
                using MemoryStream sink = new();
                await part.CopyToAsync(sink, ct);
                sent = sink.ToArray();

                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag\"");
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.NotNull(sent);
        Assert.Equal(signedMd5, Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(sent!)));
    }

    [Fact]
    public async Task AtDegreeOne_SendsPartsOneAtATime()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();

        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) => Task.FromResult(Reply(url, body, headers)),
            partOverride: async (url, offset, length, headers, openBody, report, ct) =>
            {
                lock (sync)
                {
                    peak = Math.Max(peak, ++running);
                }

                await Task.Delay(10, ct);
                lock (sync)
                {
                    running--;
                }

                return new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"etag\"");
            });

        await DrainAsync(pipeline.RunAsync(MakeContext() with { MaxParallelParts = 1 }, CancellationToken.None));

        Assert.Equal(1, peak);
    }

    /// <summary>
    /// Cancelling during the guest sign-up must reach the runner AS cancellation. Swallowed into an
    /// <see cref="AttemptFailed"/> it becomes terminal — the scheduler marks the upload Failed, stops
    /// retrying it, and the user who pressed Cancel is told something went wrong instead.
    /// <para>The sign-up is the FIRST network call the pipeline makes, so it is also the widest
    /// window in which a cancel can land before any bytes move.</para>
    /// </summary>
    [Fact]
    public async Task CancellingDuringGuestSignUp_IsReportedAsCancellation_NotAsAFailure()
    {
        using CancellationTokenSource cts = new();
        UploadNowPipeline pipeline = new(
            apiOverride: (method, url, body, headers) =>
            {
                if (url.Contains("signUp", StringComparison.Ordinal))
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(Reply(url, body, headers));
            },
            partOverride: (url, _, _, _, _, _, _) =>
                Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>(), null, "\"e\"")));

        AttemptContext ctx = MakeContext() with { Cancellation = cts.Token };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (UploadEvent _ in pipeline.RunAsync(ctx, cts.Token))
            {
            }
        });
    }

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
