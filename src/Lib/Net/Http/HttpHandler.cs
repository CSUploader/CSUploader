// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http.Headers;
using System.Text;

namespace CSUploader.Lib.Net.Http;

public class HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription, MockServerConfig mockServer, bool bypassMockServer = false) : IDisposable
{
    private readonly string _proxyDescription = string.IsNullOrEmpty(proxyDescription) ? "(direct)" : proxyDescription;
    private bool _disposed;

    /// <summary>Test-observable snapshot of the mock config locked in at construction.</summary>
    internal MockServerConfig MockServerSnapshot => mockServer;

    /// <summary>Test-only accessor — lets unit tests assert default headers (e.g. UA) configured by the factory.</summary>
    internal HttpClient ClientForTesting => HttpClient;

    private string MaybeRewriteToMockServer(string url)
    {
        if (bypassMockServer)
        {
            // Caller (e.g. proxy connectivity test) explicitly opted out of the dev
            // redirect. Don't even log the "mock disabled" line — that's only useful
            // for upload traffic.
            return url;
        }

        if (!mockServer.Enabled || string.IsNullOrEmpty(mockServer.BaseUrl))
        {
            logger.Log(this, LogType.Status, $"Mock server disabled — sending to live URL: {url}");
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? originalUri))
        {
            return url;
        }

        if (!Uri.TryCreate(mockServer.BaseUrl, UriKind.Absolute, out Uri? mockUri))
        {
            return url;
        }

        // Already pointing at the mock server — leave as-is to avoid double-rewriting
        if (string.Equals(originalUri.Host, mockUri.Host, StringComparison.OrdinalIgnoreCase)
            && originalUri.Port == mockUri.Port)
        {
            return url;
        }

        // Extract a hoster slug from the host: strip "www.", take the first DNS label, lowercase.
        // e.g. "www.rapidgator.com" → "rapidgator", "rapidgator.net" → "rapidgator"
        string host = originalUri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        int firstDot = host.IndexOf('.', StringComparison.Ordinal);
        string slug = (firstDot > 0 ? host[..firstDot] : host).ToLowerInvariant();

        string mockBase = mockServer.BaseUrl.TrimEnd('/');
        string rewritten = $"{mockBase}/{slug}{originalUri.PathAndQuery}";
        logger.Log(this, LogType.Status, $"Mock rewrite: {url} -> {rewritten}");
        return rewritten;
    }

    public event EventHandler<OperationProgressEventArgs>? UploadProgress;

    public event EventHandler<ProtocolUploadFinishedEventArgs>? UploadFinished;

    protected HttpClient HttpClient { get; } = httpclient;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HttpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        => GetStringAsync(url, headers: null, cancellationToken);

    /// <summary>
    /// GET overload that lets the caller attach per-request headers (e.g. <c>Cookie</c>
    /// for hoster pipelines that authenticate via session cookies). Header values are
    /// added with <see cref="System.Net.Http.Headers.HttpHeaders.TryAddWithoutValidation(string, string?)"/>
    /// so non-standard cookie values aren't rejected by the framework.
    /// </summary>
    public async Task<string> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        HttpTransaction transaction = new()
        {
            Method = "GET",
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, content: null, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string result = await response.Content.ReadAsStringAsync(cancellationToken);

            // Capture response
            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = result;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(result);
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);
            return result;
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = 0;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            throw;
        }
    }

    /// <summary>
    /// GETs a URL and returns the full response snapshot (status code, body, cookies)
    /// instead of just the body string. Use when the caller needs to branch on the
    /// HTTP status (e.g. FileBoom's <c>/v1/files/upload-url</c> returns 401 when the
    /// JWT cookie has expired and the pipeline needs to invalidate its cached token).
    /// </summary>
    public async Task<HttpResponseSnapshot> GetSnapshotAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        HttpTransaction transaction = new()
        {
            Method = "GET",
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, content: null, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            CaptureResponseHeaders(transaction, response);
            LogTransaction(transaction);

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString);
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = 0;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            throw;
        }
    }

    /// <summary>
    /// POSTs a form-urlencoded body and returns the response status, body, and any
    /// <c>Set-Cookie</c> headers. Unlike <see cref="GetStringAsync"/>, this does not
    /// throw on non-2xx — callers handle their own status (e.g. BRupload's login flow
    /// returns 302 on success).
    /// </summary>
    public async Task<HttpResponseSnapshot> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        using FormUrlEncodedContent content = new(form);
        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
            RequestBody = await content.ReadAsStringAsync(cancellationToken),
        };

        try
        {
            CaptureRequestHeaders(transaction, content);

            using HttpResponseMessage response = await HttpClient.PostAsync(url, content, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            CaptureResponseHeaders(transaction, response);
            LogTransaction(transaction);

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString);
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = 0;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            throw;
        }
    }

    /// <summary>
    /// POSTs a JSON body and returns the response status, body, and any <c>Set-Cookie</c>
    /// headers. Like <see cref="PostFormAsync"/>, does not throw on non-2xx — REST-style
    /// APIs (FileBoom/Keep2Share) return error envelopes with HTTP 200 alongside non-200
    /// auth-expired responses, and callers handle both shapes themselves.
    /// </summary>
    public Task<HttpResponseSnapshot> PostJsonAsync(string url, string jsonBody, CancellationToken cancellationToken = default)
        => PostJsonAsync(url, jsonBody, headers: null, cancellationToken);

    /// <summary>
    /// Like <see cref="PostJsonAsync(string, string, CancellationToken)"/> but attaches extra
    /// request headers — e.g. a forwarded <c>Cookie</c> for cookie-authenticated JSON APIs whose
    /// handler is built without <c>UseCookies</c> (HitFile's <c>/api/folder/content</c> storage
    /// re-read on refresh). Mirrors <see cref="GetSnapshotAsync(string, IReadOnlyDictionary{string, string}?, CancellationToken)"/>;
    /// does not throw on non-2xx. A null <paramref name="jsonBody"/> sends a body-less POST with no
    /// <c>Content-Type</c> (mirrors a browser <c>fetch(POST)</c> with no body — HitFile's
    /// <c>/api/user/app/id</c>), so a strict body/CSRF validator can't reject an unexpected entity.
    /// </summary>
    public async Task<HttpResponseSnapshot> PostJsonAsync(string url, string? jsonBody, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        // No `using` on content: the HttpRequestMessage below owns it and disposes it (a
        // separate using would double-dispose). Mirrors UploadMultipartAsync's note.
        StringContent? content = jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json");
        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
            RequestBody = jsonBody ?? string.Empty,
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = content };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, content, request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            CaptureResponseHeaders(transaction, response);
            LogTransaction(transaction);

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString);
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = 0;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            throw;
        }
    }

    /// <summary>
    /// Multipart POST that supports extra form fields and a custom file field name, and
    /// returns the response body. The existing <see cref="UploadFileAsync"/> is fixed to
    /// a <c>"file"</c> part with no peers and discards the response, which is fine for
    /// Rapidgator/Alfafile (their bytes upload is opaque and a separate <c>upload_info</c>
    /// call returns the result) but doesn't work for hosters like BRupload where the
    /// multipart response IS the upload result.
    /// </summary>
    public async Task<HttpResponseSnapshot> UploadMultipartAsync(
        string filePath,
        string endpoint,
        string fileFieldName,
        IReadOnlyDictionary<string, string>? extraFields = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;
        endpoint = MaybeRewriteToMockServer(endpoint);

        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = endpoint,
            Proxy = _proxyDescription,
            StartTime = dateTimeStarted,
            RequestBody = $"[Multipart file upload: {Path.GetFileName(filePath)}]",
        };

        // Hoisted out of the try so the generic catch can read BodyFullySent. Stays null until the
        // FileStream is opened and the content created — a fault while it's still null is a local
        // setup error (e.g. the source file vanished), NOT a network "nothing committed" case.
        ProgressStreamContent? progressContent = null;

        try
        {
            MultipartFormDataContent multipartContent = BuildBrowserShapedMultipart(out string _);

            if (extraFields is not null)
            {
                foreach (KeyValuePair<string, string> field in extraFields)
                {
                    AddBareStringPart(multipartContent, field.Key, field.Value);
                }
            }

            FileStream rawStream = new(filePath, FileMode.Open, FileAccess.Read);
            Stream fileStream = getBytesPerSecond is not null
                ? new ThrottledStream(rawStream, getBytesPerSecond)
                : rawStream;
            using Stream disposeFileStream = fileStream;
            progressContent = new(
                fileStream,
                (totalBytes, bytesTransferred) => UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted)),
                cancellationToken);
            AddFilePart(multipartContent, progressContent, fileFieldName, filePath);

            // HttpRequestMessage.Dispose() disposes its Content for us, so we don't keep a
            // separate `using` on multipartContent — that'd double-dispose.
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = multipartContent };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, multipartContent, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(response.IsSuccessStatusCode, body, dateTimeStarted));

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString);
        }
        catch (OperationCanceledException)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Cancelled";
            LogTransaction(transaction);
            throw;
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Error";
            // ex.ToString() preserves the inner-exception chain (e.g. an
            // HttpRequestException whose Message is "SSL connection could not be
            // established, see inner exception." is useless without that chain — the
            // real cause is in the AuthenticationException or IOException underneath).
            // ex.Message goes to the UploadFinished event arg below because that's the
            // brief summary surfaced in the per-row status, where a stack trace would
            // be noise.
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));

            // Connect-phase reclassification — intentionally scoped to UploadMultipartAsync (the
            // shared multipart path used by HitFile, GigaPeta, BRupload, and other multipart
            // hosters), NOT UploadFileAsync/PostChunkAsync. Once progressContent exists, the upload
            // attempt is underway; a fault while BodyFullySent is still false means the request
            // body didn't complete — a connect-phase DNS/TCP/TLS failure that never reached the
            // body, or a mid-send abort. Either way zero (or partial) bytes were sent, the server
            // committed nothing, so reclassify as a safe-to-retry body-transfer abort for the
            // shared retry layer. Guard on progressContent being non-null: if it was never created
            // (FileStream open failed before creation) the fault is local setup (file gone), not a
            // network case, and must stay a plain terminal fault. A fault AFTER the body was fully
            // sent (BodyFullySent true, e.g. a lost response) is likewise NOT reclassified — the
            // server may have committed, so it must not retry.
            if (progressContent is { BodyFullySent: false } && !UploadBodyTransferException.IsInChain(ex))
            {
                throw new UploadBodyTransferException(ex);
            }

            throw;
        }
    }

    /// <summary>
    /// POSTs a single chunk of a file as a multipart body to <paramref name="endpoint"/>.
    /// Body shape (matches the modern XFileSharing CDN protocol, captured from hxfile.co
    /// on 2026-06-01):
    /// <code>
    /// multipart/form-data
    ///   name="sid"   → sid
    ///   name="file"; filename="file_{chunkIndex}"; Content-Type: application/octet-stream → chunk bytes
    /// </code>
    /// Progress is reported via the same <see cref="UploadProgress"/> event the legacy
    /// path uses, but with <em>file-cumulative</em> values: <paramref name="basePosition"/>
    /// is added to bytes-read-from-the-chunk so multiple chunks emit a single contiguous
    /// progress stream from 0 to <paramref name="totalFileSize"/>.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for slicing the file (see <see cref="ChunkSliceStream"/>)
    /// and for tracking the chunk index. Each chunk is a separate HTTP transaction with
    /// its own entry in the Logs tab — the per-chunk overhead is the network round-trip,
    /// not anything inside this method.
    /// </remarks>
    public async Task<HttpResponseSnapshot> PostChunkAsync(
        string endpoint,
        string sid,
        Stream chunkData,
        long chunkLength,
        int chunkIndex,
        long basePosition,
        long totalFileSize,
        DateTime dateTimeStarted,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default)
    {
        endpoint = MaybeRewriteToMockServer(endpoint);

        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = endpoint,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
            RequestBody = $"[Chunk {chunkIndex}: {chunkLength} bytes, sid={sid}]",
        };

        try
        {
            MultipartFormDataContent multipartContent = BuildBrowserShapedMultipart(out string _);
            AddBareStringPart(multipartContent, "sid", sid);

            Stream chunkStream = getBytesPerSecond is not null
                ? new ThrottledStream(chunkData, getBytesPerSecond)
                : chunkData;
            ProgressStreamContent chunkPart = new(
                chunkStream,
                // Translate per-chunk progress to file-cumulative progress so the
                // consumer (pipeline → UI) sees one monotonically-increasing stream
                // across all chunks rather than ten short 0-to-80MB cycles.
                (_, bytesInThisChunk) => UploadProgress?.Invoke(
                    this,
                    new OperationProgressEventArgs(totalFileSize, basePosition + bytesInThisChunk, dateTimeStarted)),
                cancellationToken);
            AddChunkFilePart(multipartContent, chunkPart, chunkIndex);

            using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = multipartContent };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, multipartContent, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString);
        }
        catch (OperationCanceledException)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Cancelled";
            LogTransaction(transaction);
            throw;
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);

            // Deliberately NOT doing UploadMultipartAsync's connect-phase reclassification here:
            // chunked uploads re-discover (sid + per-chunk finalize) on each attempt and have their
            // own result-verification model, so the "body never fully sent → safe to retry" rule
            // doesn't transfer to this path. Stronger still: chunked uploads must NEVER be
            // whole-pipeline retried by the shared retry layer — each chunk is committed
            // server-side (up.cgi per chunk + api.cgi finalize), so re-sending could double-commit.
            // ProgressStreamContent wraps a mid-send chunk write failure as
            // UploadBodyTransferException one layer below us; strip that marker here so
            // AttemptRunner's IsInChain retry gate can never treat a chunk transport fault as
            // safe-to-retry. The underlying transport error still propagates (terminal). The marker
            // may be nested (HttpClient wraps content-serialization exceptions in
            // HttpRequestException("Error while copying content to a stream.", <marker>)), so walk
            // the chain.
            if (UploadBodyTransferException.IsInChain(ex))
            {
                for (Exception? e = ex; e is not null; e = e.InnerException)
                {
                    if (e is UploadBodyTransferException marker)
                    {
                        throw marker.InnerException ?? marker;
                    }
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Chunk-specific file-part shape: field name is always <c>file</c>, filename is
    /// <c>file_&lt;chunkIndex&gt;</c> (matches the hxfile.co browser capture), MIME is
    /// always <c>application/octet-stream</c> regardless of the source file's extension.
    /// Mirrors <see cref="AddFilePart"/>'s browser-quoted Content-Disposition shape.
    /// </summary>
    private static void AddChunkFilePart(MultipartFormDataContent multipart, HttpContent chunkContent, int chunkIndex)
    {
        string fileName = $"file_{chunkIndex}";
        chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        multipart.Add(chunkContent, "file");
        chunkContent.Headers.ContentDisposition = null;
        chunkContent.Headers.TryAddWithoutValidation(
            "Content-Disposition",
            $"form-data; name=\"file\"; filename=\"{fileName}\"");
    }

    private static IReadOnlyList<string> ReadSetCookies(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return [.. values];
        }

        return [];
    }

    public async Task UploadFileAsync(string filePath, string endpoint, Func<long?>? getBytesPerSecond = null, CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;

        endpoint = MaybeRewriteToMockServer(endpoint);

        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = endpoint,
            Proxy = _proxyDescription,
            StartTime = dateTimeStarted,
            RequestBody = $"[Multipart file upload: {Path.GetFileName(filePath)}]",
        };

        try
        {
            using MultipartFormDataContent multipartContent = BuildBrowserShapedMultipart(out string _);
            FileStream rawStream = new(filePath, FileMode.Open, FileAccess.Read);
            Stream fileStream = getBytesPerSecond is not null
                ? new ThrottledStream(rawStream, getBytesPerSecond)
                : rawStream;
            using var disposeFileStream = fileStream;
            var progressContent = new ProgressStreamContent(fileStream, (totalBytes, bytesTransferred) => UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted)), cancellationToken);

            AddFilePart(multipartContent, progressContent, "file", filePath);

            CaptureRequestHeaders(transaction, multipartContent);

            using HttpResponseMessage response = await HttpClient.PostAsync(endpoint, multipartContent, cancellationToken);
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = result;
            transaction.ResponseBodyBytes = System.Text.Encoding.UTF8.GetBytes(result);
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(true, result, dateTimeStarted));
        }
        catch (OperationCanceledException)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Cancelled";
            LogTransaction(transaction);
            throw;
        }
        catch (Exception ex) when (UploadBodyTransferException.IsInChain(ex))
        {
            // A mid-send body abort — log like the generic catch but RETHROW so the shared
            // retry layer (AttemptRunner) can re-send (the server committed nothing).
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Error";
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
            throw;
        }
        catch (Exception ex)
        {
            transaction.EndTime = DateTime.Now;
            transaction.StatusReason = "Error";
            // ex.ToString() preserves the inner-exception chain — see the matching catch
            // in UploadMultipartAsync above for the rationale.
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
    }

    /// <summary>
    /// Builds a <see cref="MultipartFormDataContent"/> whose Content-Type emits the boundary
    /// <em>without</em> the surrounding double quotes .NET adds by default. RFC 2046 allows
    /// quoted boundaries, but some XFileSharing-family PHP/Perl backends (notably BRupload's
    /// fs.cgi) treat the literal <c>"…"</c> as part of the delimiter and fail to find the
    /// file-part terminator. Mirrors the browser (WebKit/Chromium) which always emits the
    /// boundary unquoted.
    /// </summary>
    /// <param name="boundary">The boundary value placed in the Content-Type header (also
    /// reused as the literal multipart separator written by .NET).</param>
    private static MultipartFormDataContent BuildBrowserShapedMultipart(out string boundary)
    {
        // Use a token-only boundary (alphanumerics + dashes), browser-style four-dash prefix.
        // The ticks suffix keeps each request unique. .NET will still quote-wrap on
        // construction; we strip the quotes below.
        boundary = $"----CSUploaderBoundary{DateTime.Now.Ticks:x}";
        MultipartFormDataContent content = new(boundary);

        // Re-add the boundary parameter unquoted. NameValueHeaderValue only quotes values
        // that contain non-token characters, so a token-only boundary stays bare.
        MediaTypeHeaderValue ct = content.Headers.ContentType!;
        NameValueHeaderValue? quoted = ct.Parameters.FirstOrDefault(
            p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase));
        if (quoted is not null)
        {
            ct.Parameters.Remove(quoted);
        }

        ct.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
        return content;
    }

    /// <summary>
    /// Adds a string form field with <em>no</em> <c>Content-Type</c> header on the part and
    /// a <em>quoted</em> name in the <c>Content-Disposition</c> header.
    /// </summary>
    /// <remarks>
    /// Two reasons the disposition is rewritten by hand instead of leaving .NET's default:
    /// <list type="bullet">
    ///   <item>Browsers always emit <c>name="..."</c> with quotes. .NET only quotes when the
    ///   name contains a non-token character, so token names like <c>sess_id</c> go out
    ///   unquoted. XFileSharing's Perl multipart parser regex-extracts <c>name="(...)"</c>
    ///   and silently drops parts whose names aren't quoted — meaning <c>sess_id</c> never
    ///   reaches <c>upload.cgi</c>, the user is treated as anonymous, and the upload is
    ///   rejected with the generic "uploads are not enabled for your account type" error.</item>
    ///   <item>Some servers reject string parts that carry <c>Content-Type: text/plain;
    ///   charset=utf-8</c> (.NET's default). Browsers send these parts bare.</item>
    /// </list>
    /// </remarks>
    private static void AddBareStringPart(MultipartFormDataContent multipart, string name, string value)
    {
        StringContent part = new(value);
        part.Headers.ContentType = null;

        // .NET sets a Content-Disposition with the unquoted name. Replace it with the
        // browser-shaped, quoted version.
        multipart.Add(part, name);
        part.Headers.ContentDisposition = null;
        part.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"");
    }

    /// <summary>
    /// Attaches the file part with a <em>browser-shaped</em> <c>Content-Disposition</c>:
    /// only emits <c>filename*=utf-8''…</c> when the filename actually contains non-ASCII
    /// bytes. .NET's default <see cref="MultipartFormDataContent.Add(HttpContent, string, string)"/>
    /// adds the RFC 5987 <c>filename*</c> parameter unconditionally, and some Perl multipart
    /// parsers (XFileSharing's <c>fs.cgi</c>) misinterpret the duplicate filename and 500
    /// out. Also stamps the part with a real MIME type guessed from the extension instead
    /// of the generic <c>application/octet-stream</c>.
    /// </summary>
    private static void AddFilePart(MultipartFormDataContent multipart, HttpContent fileContent, string fieldName, string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeGuesser.Guess(filePath));

        // Add first with the (content, name) overload so .NET sets a baseline
        // Content-Disposition; then overwrite it with our cleaner version.
        multipart.Add(fileContent, fieldName);
        fileContent.Headers.ContentDisposition = null;

        string cdValue = System.Text.Ascii.IsValid(fileName)
            ? $"form-data; name=\"{fieldName}\"; filename=\"{fileName}\""
            : $"form-data; name=\"{fieldName}\"; filename=\"{fileName}\"; filename*=utf-8''{Uri.EscapeDataString(fileName)}";
        fileContent.Headers.TryAddWithoutValidation("Content-Disposition", cdValue);
    }

    private void LogTransaction(HttpTransaction transaction) => logger.Log(null, LogType.Http, transaction.Summary, httpTransaction: transaction);

    private void CaptureRequestHeaders(HttpTransaction transaction, HttpContent? content, System.Net.Http.Headers.HttpRequestHeaders? requestHeaders = null)
    {
        // Capture in three passes so per-request headers override client-default headers
        // (matches what actually goes on the wire — request.Headers wins for any header
        // that's also on DefaultRequestHeaders).
        foreach (KeyValuePair<string, IEnumerable<string>> header in HttpClient.DefaultRequestHeaders)
        {
            transaction.RequestHeaders[header.Key] = [.. header.Value];
        }

        if (requestHeaders is not null)
        {
            // Per-request headers from HttpRequestMessage.Headers (Cookie, Origin,
            // Sec-Fetch-*, custom auth bearers, etc.). Previously omitted — meant the
            // Logs tab silently misrepresented what we actually sent.
            foreach (KeyValuePair<string, IEnumerable<string>> header in requestHeaders)
            {
                transaction.RequestHeaders[header.Key] = [.. header.Value];
            }
        }

        if (content?.Headers is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
            {
                transaction.RequestHeaders[header.Key] = [.. header.Value];
            }
        }
    }

    private static void CaptureResponseHeaders(HttpTransaction transaction, HttpResponseMessage response)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            transaction.ResponseHeaders[header.Key] = [.. header.Value];
        }

        if (response.Content.Headers is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                transaction.ResponseHeaders[header.Key] = [.. header.Value];
            }
        }
    }
}
