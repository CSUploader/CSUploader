// <copyright file="HttpHandlerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net;
using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Verifies that <see cref="HttpHandler"/> stamps every captured <see cref="HttpTransaction"/>
/// with the proxy description it was constructed with — that's how the Logs tab can show
/// users which proxy a request went through. Uses a stub <see cref="HttpMessageHandler"/>
/// so the tests don't need a network.
/// </summary>
public class HttpHandlerTests
{
    [Fact]
    public async Task GetStringAsync_NullProxyDescription_LogsTransactionAsDirect()
    {
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, proxyDescription: null, MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.NotNull(capture.Transaction);
        Assert.Equal("(direct)", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_EmptyProxyDescription_LogsTransactionAsDirect()
    {
        // Defensive: callers that pass "" (rather than null) should also fall back to "(direct)".
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, proxyDescription: string.Empty, MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.Equal("(direct)", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_WithProxyDescription_LogsTransactionWithThatDescription()
    {
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, "socks5://10.0.0.1:1080", MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.Equal("socks5://10.0.0.1:1080", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_WhenRequestThrows_StillLogsTransactionWithProxyDescription()
    {
        // Failures are exactly the case where the proxy description matters most — a glance
        // at the Logs tab needs to point at the right proxy. The catch-block logging path
        // must carry the description as well.
        TransactionCapture capture = new();
        HttpClient client = ThrowingClient(new HttpRequestException("boom"));
        HttpHandler handler = new(client, capture.Logger, "http://1.2.3.4:8080", MockServerConfig.Disabled);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => handler.GetStringAsync("https://example.test/x"));

        Assert.NotNull(capture.Transaction);
        Assert.Equal("http://1.2.3.4:8080", capture.Transaction!.Proxy);
    }

    // ---- Multipart shape: locks in the browser-like body produced by UploadMultipartAsync ----
    //
    // These tests exist because the previous implementation produced an XFileSharing-incompatible
    // body (quoted boundary, dual `filename` + `filename*`, `text/plain; charset=utf-8` on every
    // string part, generic `application/octet-stream` for the file). BRupload's fs.cgi 500'd on
    // that shape even though the multipart was technically RFC-valid. If any of these regress,
    // BRupload uploads will break first.

    [Fact]
    public async Task UploadMultipartAsync_ContentTypeBoundaryIsNotQuoted()
    {
        // RFC 2046 allows `boundary="..."` but XFileSharing's Perl multipart parser treats the
        // quotes as part of the delimiter. The browser never emits the quoted form, so we don't either.
        using TempFile temp = TempFile.With("hello-bytes");
        CapturingHandler capture = new();
        HttpHandler handler = new(new HttpClient(capture), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        await handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file_0");

        string? ct = capture.RequestContentType;
        Assert.NotNull(ct);
        // Should be: multipart/form-data; boundary=----CSUploaderBoundary<hex>  — no quotes around the value.
        Assert.Contains("boundary=----CSUploaderBoundary", ct, StringComparison.Ordinal);
        Assert.DoesNotContain("boundary=\"", ct, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadMultipartAsync_AsciiFilename_OmitsFilenameStarParameter()
    {
        // Browsers only emit filename*=utf-8''... when the filename contains non-ASCII bytes.
        // .NET's MultipartFormDataContent adds it unconditionally and that confused fs.cgi.
        using TempFile temp = TempFile.With("hello-bytes", "ascii-name.mp4");
        CapturingHandler capture = new();
        HttpHandler handler = new(new HttpClient(capture), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        await handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file_0");

        string body = capture.RequestBody ?? string.Empty;
        Assert.Contains("name=\"file_0\"; filename=\"ascii-name.mp4\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("filename*=", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadMultipartAsync_NonAsciiFilename_EmitsFilenameStarFallback()
    {
        // For names that genuinely need RFC 5987, we still emit filename* as the fallback so
        // the server can recover the original bytes. Mirrors what a browser sends for the same case.
        using TempFile temp = TempFile.With("bytes", "résumé.pdf");
        CapturingHandler capture = new();
        HttpHandler handler = new(new HttpClient(capture), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        await handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file");

        string body = capture.RequestBody ?? string.Empty;
        Assert.Contains("filename*=utf-8''", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadMultipartAsync_FilePart_UsesMimeTypeGuessedFromExtension()
    {
        // application/octet-stream is the lowest-information answer and lets a server
        // confuse the upload with an unrecognised binary. Match what the browser sends.
        using TempFile temp = TempFile.With("bytes", "movie.mp4");
        CapturingHandler capture = new();
        HttpHandler handler = new(new HttpClient(capture), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        await handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file_0");

        string body = capture.RequestBody ?? string.Empty;
        // The file part's Content-Type line should reflect the real MIME for .mp4.
        Assert.Contains("Content-Type: video/mp4", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadMultipartAsync_StringParts_HaveQuotedNameAndNoContentTypeHeader()
    {
        // Two browser-parity properties bundled into one test because they live in the same
        // section of the part:
        //   1. name="sess_id" (quoted) — XFileSharing's Perl parser regex-extracts
        //      `name="(...)"` and drops unquoted parts, so an unquoted name means
        //      upload.cgi never sees sess_id → user looks anonymous → "uploads not enabled".
        //   2. No `Content-Type: text/plain; charset=utf-8` on the part — browsers send
        //      form-field parts bare and .NET's default added one.
        using TempFile temp = TempFile.With("bytes", "x.mp4");
        CapturingHandler capture = new();
        HttpHandler handler = new(new HttpClient(capture), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        Dictionary<string, string> extras = new(StringComparer.Ordinal)
        {
            ["sess_id"] = "abc",
            ["utype"] = "reg",
        };

        await handler.UploadMultipartAsync(temp.Path, "https://example.test/u",
            fileFieldName: "file_0",
            extraFields: extras);

        string body = capture.RequestBody ?? string.Empty;

        // 1. Quoted name in Content-Disposition (browser-style).
        Assert.Contains("name=\"sess_id\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"utype\"", body, StringComparison.Ordinal);
        // Regression: the unquoted form must NOT appear (it'd mean we left .NET's default,
        // which XFileSharing silently drops).
        Assert.DoesNotContain("name=sess_id\r\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=sess_id\n", body, StringComparison.Ordinal);

        // 2. No Content-Type between the quoted-name disposition and the value.
        int sessIdx = body.IndexOf("name=\"sess_id\"", StringComparison.Ordinal);
        int valueIdx = body.IndexOf("abc", sessIdx, StringComparison.Ordinal);
        Assert.True(sessIdx >= 0 && valueIdx > sessIdx, $"sess_id part malformed. Body:\n{body}");
        string between = body[sessIdx..valueIdx];
        Assert.DoesNotContain("Content-Type:", between, StringComparison.Ordinal);
    }

    // ---- Connect-phase retry classification: UploadMultipartAsync reclassifies a fault where the
    // request body was never fully sent (connect-phase DNS/TCP/TLS failure that never reached the
    // body) as a safe-to-retry UploadBodyTransferException, but leaves a post-body failure (server
    // may have committed) as a terminal fault. ----

    [Fact]
    public async Task UploadMultipartAsync_ConnectPhaseFailure_ReclassifiedAsRetryableBodyTransferAbort()
    {
        // The inner handler throws WITHOUT ever reading the request content — simulating a
        // connect-phase failure (DNS/TCP/TLS) where ProgressStreamContent.SerializeToStreamAsync
        // never runs, so BodyFullySent stays false. Zero bytes were sent → server committed
        // nothing → the shared retry layer must be allowed to re-send.
        using TempFile temp = TempFile.With("payload-bytes");
        HttpHandler handler = new(
            new HttpClient(new ConnectFailHandler(new HttpRequestException("No connection could be made"))),
            Mock.Of<IAppLogger>(),
            null,
            MockServerConfig.Disabled);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file"));

        Assert.True(
            UploadBodyTransferException.IsInChain(thrown),
            $"Connect-phase failure should be reclassified as a retryable body-transfer abort. Got: {thrown}");
    }

    [Fact]
    public async Task UploadMultipartAsync_PostBodyFailure_NotReclassified_StaysTerminalFault()
    {
        // SAFETY TEST: the inner handler FIRST drains the request content (so ProgressStreamContent
        // runs to completion and sets BodyFullySent), THEN throws — simulating the response being
        // lost AFTER the whole body was sent. The server may have committed the upload, so this
        // must NOT be reclassified as retryable, or AttemptRunner could double-create the file.
        using TempFile temp = TempFile.With("payload-bytes");
        HttpHandler handler = new(
            new HttpClient(new DrainThenThrowHandler(new HttpRequestException("connection closed while receiving response"))),
            Mock.Of<IAppLogger>(),
            null,
            MockServerConfig.Disabled);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => handler.UploadMultipartAsync(temp.Path, "https://example.test/u", fileFieldName: "file"));

        Assert.False(
            UploadBodyTransferException.IsInChain(thrown),
            $"A failure after the body was fully sent must stay a terminal fault (server may have committed). Got: {thrown}");
    }

    [Fact]
    public async Task UploadMultipartAsync_LocalFileOpenFails_NotReclassified_StaysTerminalFault()
    {
        // SAFETY NEGATIVE for the `progressContent is not null` half of the guard: a non-existent
        // source file makes `new FileStream(...)` throw BEFORE progressContent is assigned (it stays
        // null), so the fault is a LOCAL setup error, not a network "nothing committed" case. It must
        // NOT be reclassified as retryable. Locks the guard against a future refactor that eagerly
        // initializes progressContent. The inner handler never even runs — the throw is pre-send.
        string missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "csu-does-not-exist-" + Guid.NewGuid().ToString("N") + ".bin");
        HttpHandler handler = new(
            new HttpClient(new ConnectFailHandler(new HttpRequestException("should never be reached"))),
            Mock.Of<IAppLogger>(),
            null,
            MockServerConfig.Disabled);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => handler.UploadMultipartAsync(missingPath, "https://example.test/u", fileFieldName: "file"));

        Assert.False(
            UploadBodyTransferException.IsInChain(thrown),
            $"A local file-open failure (progressContent never created) must stay a terminal fault, not be reclassified as retryable. Got: {thrown}");
    }

    // ---- Chunked uploads must NEVER be whole-pipeline retried by the shared retry layer (each
    // chunk commits server-side: up.cgi per chunk + api.cgi finalize → re-sending could
    // double-commit). PostChunkAsync therefore STRIPS any UploadBodyTransferException marker so the
    // AttemptRunner IsInChain gate can never classify a chunk transport fault as safe-to-retry. ----

    [Fact]
    public async Task PostChunkAsync_BodyTransferMarker_IsStrippedSoChunkedIsNeverRetryable()
    {
        // The inner handler throws a PRE-WRAPPED marker exactly as the real stack produces it:
        // HttpClient wraps a content-serialization fault (ProgressStreamContent's mid-send chunk
        // write abort → UploadBodyTransferException) in
        // HttpRequestException("Error while copying content to a stream.", <marker>). PostChunkAsync
        // must strip that marker so the fault can never be treated as retryable, while preserving
        // the underlying transport cause.
        HttpHandler handler = new(
            new HttpClient(new ThrowPreWrappedMarkerHandler(
                new HttpRequestException(
                    "Error while copying content to a stream.",
                    new UploadBodyTransferException(new IOException("connection reset"))))),
            Mock.Of<IAppLogger>(),
            null,
            MockServerConfig.Disabled);

        using MemoryStream chunk = new("chunk-bytes"u8.ToArray());

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => handler.PostChunkAsync(
                endpoint: "https://example.test/up.cgi",
                sid: "sid-123",
                chunkData: chunk,
                chunkLength: chunk.Length,
                chunkIndex: 0,
                basePosition: 0,
                totalFileSize: chunk.Length,
                dateTimeStarted: DateTime.Now));

        // The marker was stripped — chunked can never be whole-pipeline retried.
        Assert.False(
            UploadBodyTransferException.IsInChain(thrown),
            $"PostChunkAsync must strip the body-transfer marker so chunked uploads are never retried. Got: {thrown}");

        // Independently prove the strip: the thrown exception IS the underlying IOException, not a
        // wrapper that still carries the marker. (A loose ToString().Contains("connection reset")
        // would pass even if the marker were still present, since the marker's ToString includes
        // its inner — so assert the concrete type and message instead.)
        IOException io = Assert.IsType<IOException>(thrown);
        Assert.Equal("connection reset", io.Message);
    }

    /// <summary>
    /// Simulates a connect-phase failure: throws on send WITHOUT ever reading the request content,
    /// so <see cref="ProgressStreamContent"/> never serializes and <c>BodyFullySent</c> stays false.
    /// </summary>
    private sealed class ConnectFailHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(toThrow);
    }

    /// <summary>
    /// Throws a caller-supplied (already-wrapped) exception on send, mirroring how the real HttpClient
    /// surfaces a content-serialization fault: HttpRequestException("Error while copying content to a
    /// stream.", &lt;UploadBodyTransferException&gt;). Used to verify PostChunkAsync strips the marker.
    /// </summary>
    private sealed class ThrowPreWrappedMarkerHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(toThrow);
    }

    /// <summary>
    /// Simulates a post-body failure (e.g. lost response): fully reads the request content first
    /// (so <see cref="ProgressStreamContent"/> runs to completion and sets <c>BodyFullySent</c>),
    /// THEN throws.
    /// </summary>
    private sealed class DrainThenThrowHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            }

            throw toThrow;
        }
    }

    /// <summary>
    /// Captures the first outbound request — its Content-Type and a fully-buffered copy of
    /// the body — so tests can assert on the on-the-wire shape. Returns 200 to keep the
    /// caller's code path uneventful.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestContentType { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestContentType = request.Content.Headers.ContentType?.ToString();

                // ReadAsByteArrayAsync buffers the entire body (including the streamed file
                // part). Files used in tests are tiny so this is fine.
                byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                RequestBody = System.Text.Encoding.UTF8.GetString(bytes);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        }
    }

    /// <summary>Temporary on-disk file with arbitrary content; deleted on dispose.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        private TempFile(string path) => Path = path;

        public static TempFile With(string content, string? fileName = null)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csu-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, fileName ?? "file.bin");
            File.WriteAllText(path, content);
            return new TempFile(path);
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
                // Best-effort cleanup — TEMP gets emptied eventually anyway.
            }
        }
    }

    private static HttpClient StubClient(HttpStatusCode status, string body) =>
        new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
            ReasonPhrase = status.ToString(),
        })));

    private static HttpClient ThrowingClient(Exception ex) =>
        new(new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(ex)));

    /// <summary>
    /// Captures the first <see cref="HttpTransaction"/> passed to <see cref="IAppLogger.Log"/>
    /// with <see cref="LogType.Http"/>. Status logs (e.g. the mock-server-disabled message)
    /// are ignored because they pass <c>null</c> for the transaction.
    /// </summary>
    private sealed class TransactionCapture
    {
        public HttpTransaction? Transaction { get; private set; }

        public IAppLogger Logger { get; }

        public TransactionCapture()
        {
            Mock<IAppLogger> mock = new();
            mock.Setup(l => l.Log(
                    It.IsAny<object?>(),
                    It.IsAny<LogType>(),
                    It.IsAny<string>(),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()))
                .Callback<object?, LogType, string, HttpTransaction?, string, string, int>(
                    (_, _, _, tx, _, _, _) =>
                    {
                        if (tx is not null && Transaction is null)
                        {
                            Transaction = tx;
                        }
                    });
            Logger = mock.Object;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => impl(request, cancellationToken);
    }
}
