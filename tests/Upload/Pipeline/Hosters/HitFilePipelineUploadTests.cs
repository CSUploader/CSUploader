// <copyright file="HitFilePipelineUploadTests.cs" company="CSUploader">
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

public class HitFilePipelineUploadTests
{
    private const string DiscoveryUrl = "https://app.hitfile.net/api/upload/urls";
    private const string UploadServer = "https://s347.hitfile.net/uploadfile";

    [Fact]
    public async Task RunAsync_Anonymous_PostsMultipartWithoutUserIdAndReturnsLink()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"result":true,"id":"sZfsKZq","message":"Everything is ok"}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out List<DiscoveryCall> discoveries, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // Link is the bare-code share URL.
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://hitfile.net/sZfsKZq", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(discovery);
        Assert.Empty(uploads);

        // Discovery: POST {"count":1} to the API.
        DiscoveryCall disc = Assert.Single(discoveries);
        Assert.Equal(DiscoveryUrl, disc.Url);
        Assert.Equal("""{"count":1}""", disc.Body);

        // Upload: posted to the discovered server with the anonymous fields, no user_id, no cookies.
        UploadCall call = Assert.Single(calls);
        Assert.Equal(UploadServer, call.Endpoint);
        Assert.Equal("fd2", call.ExtraFields["apptype"]);
        Assert.Equal("0", call.ExtraFields["folder_id"]);
        Assert.False(call.ExtraFields.ContainsKey("user_id")); // anonymous => no account link
        Assert.NotNull(call.Headers);
        Assert.Equal("https://hitfile.net", call.Headers!["Origin"]);
        Assert.Equal("https://hitfile.net/", call.Headers["Referer"]);
        Assert.False(call.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task RunAsync_RegisteredAccount_SendsUserIdEqualToStoredAppId()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"result":true,"id":"Acct123","message":"Everything is ok"}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out _, out List<UploadCall> calls);

        const string AppId = "D2A1336FBEB989D9692A02F45EC60F59";
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(AppId), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://hitfile.net/Acct123", tc.FileUrl);

        // Account upload adds user_id=<appId>; everything else matches the anonymous shape.
        UploadCall call = Assert.Single(calls);
        Assert.Equal(AppId, call.ExtraFields["user_id"]);
        Assert.Equal("fd2", call.ExtraFields["apptype"]);
        Assert.Equal("0", call.ExtraFields["folder_id"]);
    }

    [Fact]
    public async Task RunAsync_AccountSelectedButNotSignedIn_FailsBeforeAnyHttp()
    {
        // Non-anonymous credential with no stored appId (never signed in) must fail fast —
        // never silently fall through to an anonymous upload.
        HitFilePipeline pipeline = MakePipeline(discovery: new(), uploads: new(), out List<DiscoveryCall> discoveries, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAccountContext(appId: null), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(discoveries); // never reached discovery
        Assert.Empty(calls);       // never reached upload
    }

    [Fact]
    public async Task RunAsync_DiscoveryReturnsNoUrls_YieldsAttemptFailedWithoutUpload()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, """{"urls":[]}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads: new(), out _, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached the upload step
    }

    [Fact]
    public async Task RunAsync_DiscoveryNon200_YieldsAttemptFailedWithoutUpload()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(503, "<html>Service Unavailable</html>", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads: new(), out _, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RunAsync_UploadResultFalse_SurfacesServerMessage()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"result":false,"message":"File is too big"}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out _, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("File is too big", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadReturnsUnexpectedBody_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(502, "<html><head><title>502 Bad Gateway</title></head></html>", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out _, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("502", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    // The next three lock in clean handling of malformed-but-HTTP-200 JSON: a type-mismatched
    // element would make JsonElement.GetString() throw InvalidOperationException (not
    // JsonException) and escape the pipeline as a raw crash unless ValueKind-guarded. Each must
    // yield exactly one AttemptFailed (no crash escaping the iterator, no TransferCompleted).

    [Fact]
    public async Task RunAsync_DiscoveryUrlElementNotString_YieldsAttemptFailedWithoutUpload()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, """{"urls":[123]}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads: new(), out _, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Empty(calls); // never reached upload
    }

    [Fact]
    public async Task RunAsync_UploadIdNotString_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"result":true,"id":12345}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out _, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadMessageNotString_YieldsAttemptFailed()
    {
        Queue<HttpResponseSnapshot> discovery = new(new[]
        {
            new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
        });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, """{"result":false,"message":{}}""", Array.Empty<string>()),
        });
        HitFilePipeline pipeline = MakePipeline(discovery, uploads, out _, out _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    // A mid-stream connection reset, shaped exactly like the live failure: HttpRequestException ->
    // UploadBodyTransferException -> IOException -> SocketException(10054). The body-transfer marker
    // is what AttemptRunner keys its safe-to-retry decision off.
    private static HttpRequestException ConnectionReset() =>
        new("Error while copying content to a stream",
            new UploadBodyTransferException(
                new IOException("Unable to write data to the transport connection",
                    new SocketException(10054))));

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // The pipeline no longer owns retry/classification — the shared retry layer (AttemptRunner)
        // does. A transport fault from the upload (a body-incomplete mid-send reset) must therefore
        // PROPAGATE out of RunAsync rather than be swallowed into an AttemptFailed/AttemptCancelled,
        // so AttemptRunner can classify it (body-not-fully-sent → re-run the whole pipeline).
        int uploadCalls = 0;
        HitFilePipeline pipeline = new(
            postJsonOverride: (url, body) =>
                new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
            uploadOverride: (filePath, endpoint, extraFields, headers, speed) =>
            {
                uploadCalls++;
                throw ConnectionReset();
            });

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex)); // the safe-to-retry signal survives intact
        Assert.Equal(1, uploadCalls); // single-shot; no in-pipeline retry
    }

    [Fact]
    public async Task RunAsync_NonTransportUploadException_PropagatesOutOfRunAsync()
    {
        // The pipeline no longer classifies faults — a non-transport throw (e.g. a vanished local
        // file) propagates exactly like a transport one and AttemptRunner decides it's non-retryable.
        int uploadCalls = 0;
        HitFilePipeline pipeline = new(
            postJsonOverride: (url, body) =>
                new HttpResponseSnapshot(200, $$"""{"urls":["{{UploadServer}}"]}""", Array.Empty<string>()),
            uploadOverride: (filePath, endpoint, extraFields, headers, speed) =>
            {
                uploadCalls++;
                throw new FileNotFoundException("the file is gone");
            });

        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.Equal(1, uploadCalls); // single-shot
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

    private static HitFilePipeline MakePipeline(
        Queue<HttpResponseSnapshot> discovery,
        Queue<HttpResponseSnapshot> uploads,
        out List<DiscoveryCall> discoveryCalls,
        out List<UploadCall> uploadCalls)
    {
        List<DiscoveryCall> discCaptured = [];
        List<UploadCall> upCaptured = [];
        discoveryCalls = discCaptured;
        uploadCalls = upCaptured;

        return new HitFilePipeline(
            postJsonOverride: (url, body) =>
            {
                discCaptured.Add(new DiscoveryCall(url, body));
                return discovery.Dequeue();
            },
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                upCaptured.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(extraFields),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });
    }

    private sealed record DiscoveryCall(string Url, string Body);

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);

    // Anonymous context: the wizard's synthetic IsAnonymous DTO (no account selected).
    private static AttemptContext MakeContext(long fileSize = 1_048_576L) =>
        MakeContext(new FileHosterLoginDto { FileHosterName = "HitFile", IsAnonymous = true }, fileSize);

    // Registered-account context: a real (non-anonymous) DTO carrying the bootstrapped appId in
    // the ApiKey slot (null appId models an account that was added but never signed in).
    private static AttemptContext MakeAccountContext(string? appId, long fileSize = 1_048_576L) =>
        MakeContext(new FileHosterLoginDto { FileHosterName = "HitFile", IsAnonymous = false, ApiKey = appId }, fileSize);

    private static AttemptContext MakeContext(FileHosterLoginDto credentials, long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\1mb.bin",
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "HitFile",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
