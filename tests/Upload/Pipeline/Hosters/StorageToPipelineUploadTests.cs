// <copyright file="StorageToPipelineUploadTests.cs" company="CSUploader">
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
/// Orchestration tests for <see cref="StorageToPipeline"/> — the four-step anonymous flow (bootstrap →
/// init-batch → R2 PUT → confirm-batch). The network is stubbed via the internal test ctor, so these
/// lock in the event sequence, the CSRF/cookie forwarding (incl. the rotated session cookie reaching
/// confirm), the share URL, and each failure branch. Captured live 2026-06-29.
/// </summary>
public class StorageToPipelineUploadTests
{
    private const string HomeHtml = """
        <!DOCTYPE html><html><head>
        <meta name="csrf-token" content="TESTCSRF123">
        </head><body>storage.to</body></html>
        """;

    private const string InitJson = """
        {"success":true,"results":{"0":{"success":true,"type":"single","upload_url":"https://abc.r2.cloudflarestorage.com/storage-to/KEY-UUID?X-Amz-Signature=sig","headers":{"Host":["abc.r2.cloudflarestorage.com"]},"r2_key":"KEY-UUID"}}}
        """;

    private const string ConfirmJson = """
        {"success":true,"results":{"0":{"success":true,"file":{"id":"qTKjLKmo1","url":"https://storage.to/qTKjLKmo1"},"owner_token":"owner_v1_xyz"}}}
        """;

    [Fact]
    public void Properties_DeclareStorageToConfig()
    {
        // The pipeline class is retained while storage.to is disabled (see below) — this keeps that
        // retention honest: the config still compiles and reads as it did when it worked.
        StorageToPipeline pipeline = new();
        Assert.Equal("Storage.to", pipeline.Name);
        Assert.Equal(25L * 1000 * 1000 * 1000, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // Sentinel: if this starts failing, someone re-added "Storage.to" to
        // FileHosterClient.FileHosters. Before flipping it to Assert.True, walk StorageToPipeline.cs's
        // re-enable checklist — the registry entry is one of three touchpoints (DI registration in
        // ServiceRegistration.cs + this test also need to flip), and storage.to's Cloudflare managed
        // challenge must be confirmed gone upstream. DISABLED 2026-08-16.
        StorageToPipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Fact]
    public async Task RunAsync_HappyPath_BootstrapsInitsPutsConfirmsAndReturnsLink()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new HttpResponseSnapshot(200, HomeHtml, ["XSRF-TOKEN=xsrf1; path=/", "storageto-session=sess1; path=/; httponly"]),
            init: new HttpResponseSnapshot(200, InitJson, ["storageto-session=sess2; path=/; httponly"]), // session rotates here
            confirm: new HttpResponseSnapshot(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferProgress);
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://storage.to/qTKjLKmo1", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // init-batch body carries the file metadata + content type guessed from the extension.
        Assert.Contains("\"filename\":\"1mb.bin\"", calls.InitBody, StringComparison.Ordinal);
        Assert.Contains("\"size\":1048576", calls.InitBody, StringComparison.Ordinal);
        Assert.Contains("\"content_type\":\"application/octet-stream\"", calls.InitBody, StringComparison.Ordinal);
        Assert.Contains("\"as_temp\":false", calls.InitBody, StringComparison.Ordinal);

        // The PUT goes verbatim to the presigned R2 URL with the same content type.
        Assert.Equal("https://abc.r2.cloudflarestorage.com/storage-to/KEY-UUID?X-Amz-Signature=sig", calls.PutUrl);
        Assert.Equal("application/octet-stream", calls.PutContentType);

        // confirm-batch echoes the r2_key from init-batch.
        Assert.Contains("\"r2_key\":\"KEY-UUID\"", calls.ConfirmBody, StringComparison.Ordinal);

        // CSRF token forwarded on both API POSTs; the ROTATED session cookie (sess2 from init's
        // response) reaches confirm, alongside the original XSRF-TOKEN.
        Assert.Equal("TESTCSRF123", calls.InitHeaders!["X-CSRF-TOKEN"]);
        Assert.Equal("TESTCSRF123", calls.ConfirmHeaders!["X-CSRF-TOKEN"]);
        Assert.Contains("storageto-session=sess2", calls.ConfirmHeaders["Cookie"], StringComparison.Ordinal);
        Assert.Contains("XSRF-TOKEN=xsrf1", calls.ConfirmHeaders["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FileExceedsAnonymousCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(calls, new(200, HomeHtml, []), new(200, InitJson, []), new(200, ConfirmJson, []), 200);

        AttemptContext ctx = MakeContext(fileSize: (25L * 1000 * 1000 * 1000) + 1);
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("storage.to", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Null(calls.GetUrl); // pre-check fired before the homepage GET
    }

    [Fact]
    public async Task RunAsync_HomepageWithoutCsrfToken_YieldsAttemptFailedWithoutUpload()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new HttpResponseSnapshot(200, "<html><body>no csrf here</body></html>", []),
            init: new(200, InitJson, []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Null(calls.PutUrl); // never reached the upload
    }

    [Fact]
    public async Task RunAsync_InitBatchReportsFailure_YieldsAttemptFailedWithoutPut()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, """{"success":false,"message":"rejected"}""", []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Null(calls.PutUrl);
    }

    // Large files: storage.to returns a multipart R2 upload (initial_urls = partNumber→presigned PUT).
    private const string MultipartInitJson =
        """{"success":true,"results":{"0":{"success":true,"type":"multipart","upload_id":"UPID-1","r2_key":"R2KEY","part_size":33554432,"total_parts":3,"initial_urls":{"1":"https://r2.mock/p1","2":"https://r2.mock/p2","3":"https://r2.mock/p3"},"owner_token":"owner_v1_x"}}}""";

    [Fact]
    public async Task RunAsync_MultipartUpload_PutsEveryPart_Completes_AndConfirms()
    {
        StorageToCalls calls = new();
        List<(string Url, int Part)> partPuts = [];
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, MultipartInitJson, []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200, // single-PUT path unused
            putPart: (url, partNumber) =>
            {
                partPuts.Add((url, partNumber));
                // R2 part PUT → 200 with the ETag response header (quoted, verbatim).
                return new HttpResponseSnapshot(200, string.Empty, [], ETag: "\"etag" + partNumber + "\"");
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // Completed with the confirm-batch share link.
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://storage.to/qTKjLKmo1", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // All 3 parts PUT to their presigned URLs, in part order.
        Assert.Equal([("https://r2.mock/p1", 1), ("https://r2.mock/p2", 2), ("https://r2.mock/p3", 3)], partPuts);

        // complete-multipart carried the upload_id + every part's ETag.
        Assert.NotNull(calls.CompleteBody);
        Assert.Contains("\"upload_id\":\"UPID-1\"", calls.CompleteBody!, StringComparison.Ordinal);
        Assert.Contains("etag1", calls.CompleteBody!, StringComparison.Ordinal);
        Assert.Contains("etag3", calls.CompleteBody!, StringComparison.Ordinal);
        Assert.Contains("\"partNumber\":3", calls.CompleteBody!, StringComparison.Ordinal);
        Assert.Equal("TESTCSRF123", calls.CompleteHeaders!["X-CSRF-TOKEN"]); // CSRF forwarded like the other API calls

        // confirm-batch still ran (with the r2_key from init).
        Assert.NotNull(calls.ConfirmBody);
        Assert.Contains("R2KEY", calls.ConfirmBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MultipartPartRejected_FailsWithoutCompleteOrConfirm()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, MultipartInitJson, []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200,
            putPart: (url, partNumber) => partNumber == 1
                ? new HttpResponseSnapshot(200, string.Empty, [], ETag: "\"e1\"")
                : new HttpResponseSnapshot(403, "<Error>AccessDenied</Error>", [])); // part 2 rejected

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("part 2 rejected", fail.Reason, StringComparison.Ordinal);
        Assert.Null(calls.CompleteBody); // never reached complete-multipart
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_MultipartPartTransportFault_PropagatesOutOfRunAsync()
    {
        // A part-PUT mid-send reset must PROPAGATE (exactly like the single path) so the shared retry layer
        // re-runs against a fresh init-batch — nothing is committed until complete-multipart + confirm-batch.
        StorageToCalls calls = new();
        int partCalls = 0;
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, MultipartInitJson, []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200,
            putPart: (_, _) =>
            {
                partCalls++;
                throw new HttpRequestException(
                    "Error while copying content to a stream",
                    new UploadBodyTransferException(
                        new IOException("Unable to write data to the transport connection", new SocketException(10054))));
            });

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex)); // the safe-to-retry signal survives
        Assert.Equal(1, partCalls); // failed on the first part; no in-pipeline retry
        Assert.Null(calls.CompleteBody); // never reached complete-multipart
    }

    [Fact]
    public async Task RunAsync_R2PutNon2xx_YieldsAttemptFailedWithoutConfirm()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new(200, InitJson, []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 403);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("403", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Null(calls.ConfirmBody); // confirm-batch never ran
    }

    [Fact]
    public async Task RunAsync_ConfirmBatchReturnsNoLink_YieldsAttemptFailed()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new(200, InitJson, []),
            confirm: new HttpResponseSnapshot(200, """{"success":true,"results":{"0":{"success":true}}}""", []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_PutTransportFault_PropagatesOutOfRunAsync()
    {
        // A body-incomplete mid-send reset must PROPAGATE out of RunAsync (not become a terminal
        // AttemptFailed) so the shared retry layer can re-run against a fresh init-batch — the confirm
        // step never ran, so nothing was committed.
        Queue<HttpResponseSnapshot> home = new([new HttpResponseSnapshot(200, HomeHtml, [])]);
        int putCalls = 0;
        StorageToPipeline pipeline = new(
            getOverride: _ => home.Dequeue(),
            postJsonOverride: (_, _, _) => new HttpResponseSnapshot(200, InitJson, []),
            putOverride: (_, _, _, _) =>
            {
                putCalls++;
                throw new HttpRequestException(
                    "Error while copying content to a stream",
                    new UploadBodyTransferException(
                        new IOException("Unable to write data to the transport connection", new SocketException(10054))));
            });

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex)); // the safe-to-retry signal survives
        Assert.Equal(1, putCalls); // single-shot; no in-pipeline retry
    }

    [Fact]
    public async Task RunAsync_InitBatchRequestThrows_YieldsAttemptFailedWithoutPut()
    {
        StorageToCalls calls = new();
        Queue<HttpResponseSnapshot> home = new([new HttpResponseSnapshot(200, HomeHtml, [])]);
        StorageToPipeline pipeline = new(
            getOverride: _ => home.Dequeue(),
            postJsonOverride: (_, _, _) => throw new HttpRequestException("init network down"),
            putOverride: (_, url, _, progress) =>
            {
                calls.PutUrl = url;
                progress(1L, 1L);
                return new HttpResponseSnapshot(200, string.Empty, []);
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("init-batch", fail.Reason, StringComparison.Ordinal);
        Assert.Null(calls.PutUrl); // the request threw before the PUT
    }

    [Fact]
    public async Task RunAsync_InitBatchMissingUploadUrl_YieldsAttemptFailedWithoutPut()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, """{"success":true,"results":{"0":{"success":true,"r2_key":"K"}}}""", []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Null(calls.PutUrl);
    }

    [Fact]
    public async Task RunAsync_InitBatchPerEntryFailure_SurfacesServerReason()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, """{"success":true,"results":{"0":{"success":false,"message":"file type not allowed"}}}""", []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("file type not allowed", fail.Reason, StringComparison.Ordinal);
        Assert.Null(calls.PutUrl);
    }

    [Fact]
    public async Task RunAsync_InitBatchMalformedJson_YieldsAttemptFailed()
    {
        StorageToCalls calls = new();
        StorageToPipeline pipeline = MakePipeline(
            calls,
            home: new(200, HomeHtml, []),
            init: new HttpResponseSnapshot(200, "<html>not json at all</html>", []),
            confirm: new(200, ConfirmJson, []),
            putStatus: 200);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Null(calls.PutUrl);
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

    private static StorageToPipeline MakePipeline(
        StorageToCalls calls,
        HttpResponseSnapshot home,
        HttpResponseSnapshot init,
        HttpResponseSnapshot confirm,
        int putStatus,
        HttpResponseSnapshot? complete = null,
        Func<string, int, HttpResponseSnapshot>? putPart = null)
    {
        return new StorageToPipeline(
            getOverride: url =>
            {
                calls.GetUrl = url;
                return home;
            },
            postJsonOverride: (url, body, headers) =>
            {
                if (url.Contains("init-batch", StringComparison.Ordinal))
                {
                    calls.InitBody = body;
                    calls.InitHeaders = new Dictionary<string, string>(headers);
                    return init;
                }

                if (url.Contains("complete-multipart", StringComparison.Ordinal))
                {
                    calls.CompleteBody = body;
                    calls.CompleteHeaders = new Dictionary<string, string>(headers);
                    return complete ?? new HttpResponseSnapshot(200, """{"success":true,"r2_key":"R2KEY"}""", []);
                }

                calls.ConfirmBody = body;
                calls.ConfirmHeaders = new Dictionary<string, string>(headers);
                return confirm;
            },
            putOverride: (filePath, url, contentType, progress) =>
            {
                calls.PutFilePath = filePath;
                calls.PutUrl = url;
                calls.PutContentType = contentType;
                progress(1048576L, 1048576L); // drive the TransferProgress bridge
                return new HttpResponseSnapshot(putStatus, putStatus == 200 ? string.Empty : "<Error>AccessDenied</Error>", []);
            },
            putPartOverride: putPart);
    }

    private sealed class StorageToCalls
    {
        public string? GetUrl { get; set; }

        public string? InitBody { get; set; }

        public Dictionary<string, string>? InitHeaders { get; set; }

        public string? ConfirmBody { get; set; }

        public Dictionary<string, string>? ConfirmHeaders { get; set; }

        public string? CompleteBody { get; set; }

        public Dictionary<string, string>? CompleteHeaders { get; set; }

        public string? PutUrl { get; set; }

        public string? PutContentType { get; set; }

        public string? PutFilePath { get; set; }
    }

    // Anonymous context: a blank login DTO (no username), exactly what the wizard builds for an
    // anonymous-capable hoster with no selected account. The .bin extension guesses to
    // application/octet-stream (MimeTypeGuesser's fallback).
    private static AttemptContext MakeContext(long fileSize = 1_048_576L) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\1mb.bin",
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "Storage.to",
        Credentials = new FileHosterLoginDto { FileHosterName = "Storage.to", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
