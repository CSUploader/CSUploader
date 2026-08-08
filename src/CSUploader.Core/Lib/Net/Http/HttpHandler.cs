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
    /// added with <see cref="HttpHeaders.TryAddWithoutValidation(string, string?)"/>
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
    public Task<HttpResponseSnapshot> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken = default)
        => PostFormAsync(url, form, headers: null, cancellationToken);

    /// <summary>
    /// POSTs an <c>application/x-www-form-urlencoded</c> body with optional per-request headers
    /// (e.g. a <c>Cookie</c> for a login that validates a page-scoped token against a session cookie —
    /// MediaFire's <c>client_login</c>). Like the no-header overload, doesn't throw on non-2xx.
    /// </summary>
    public async Task<HttpResponseSnapshot> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        // The HttpRequestMessage owns the content and disposes it; no separate `using`.
        FormUrlEncodedContent content = new(form);
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
    /// POSTs string fields as browser-shaped <c>multipart/form-data</c> with NO file part — the shape
    /// some endpoints insist on even when nothing is being uploaded (FILEAXA's <c>api.cgi
    /// op=import_file</c> finalise is sent as multipart by the site's own JS, while its sibling
    /// filehoster.io sends the same operation form-urlencoded). Same browser-shaped writer as the
    /// file-carrying methods, so part headers match what a browser emits. Doesn't throw on non-2xx.
    /// </summary>
    public async Task<HttpResponseSnapshot> PostMultipartAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
            RequestBody = string.Join("&", fields.Select(f => $"{f.Key}={f.Value}")),
        };

        try
        {
            using MultipartFormDataContent multipart = BuildBrowserShapedMultipart(out string _);
            foreach (KeyValuePair<string, string> field in fields)
            {
                AddBareStringPart(multipart, field.Key, field.Value);
            }

            using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = multipart };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, multipart, request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
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
    public Task<HttpResponseSnapshot> PostJsonAsync(string url, string? jsonBody, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Post, url, jsonBody, headers, cancellationToken);

    /// <summary>Like <see cref="PostJsonAsync(string, string?, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
    /// but for any HTTP method — e.g. a <c>PATCH</c> with a JSON body + a bearer <c>Authorization</c>
    /// header (wormhole.app's room manifest / heartbeat). Does not throw on non-2xx.</summary>
    /// <param name="jsonCharsetUtf8">When true (default) the body's Content-Type is
    /// <c>application/json; charset=utf-8</c> — .NET's <see cref="StringContent"/> default. When false it
    /// is the bare <c>application/json</c> (RFC-8259 form; JSON is always UTF-8, so the parameter is
    /// redundant). Some strict servers — File Garden's <c>/token</c> — reject the charset parameter and
    /// fail to parse the body, so those callers opt out.</param>
    public async Task<HttpResponseSnapshot> SendJsonAsync(HttpMethod method, string url, string? jsonBody, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken, bool jsonCharsetUtf8 = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        url = MaybeRewriteToMockServer(url);

        // No `using` on content: the HttpRequestMessage below owns it and disposes it (a
        // separate using would double-dispose). Mirrors UploadMultipartAsync's note.
        StringContent? content = jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json");
        if (content is not null && !jsonCharsetUtf8)
        {
            // Drop the "; charset=utf-8" parameter — a bare application/json (the request bytes stay UTF-8).
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        HttpTransaction transaction = new()
        {
            Method = method.Method,
            Url = url,
            Proxy = _proxyDescription,
            StartTime = DateTime.Now,
            RequestBody = jsonBody ?? string.Empty,
        };

        try
        {
            using HttpRequestMessage request = new(method, url) { Content = content };
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
    /// PUTs a file's raw bytes as the request body to <paramref name="endpoint"/> (no multipart
    /// wrapper) — the shape an S3-compatible <em>presigned</em> upload URL expects (e.g. storage.to's
    /// Cloudflare R2 target, whose query-string signature covers only the host, so the bytes go up
    /// with a plain <c>Content-Type</c> and no auth header). Progress is reported via the shared
    /// <see cref="UploadProgress"/> event, and a connect-phase/mid-send transport fault is reclassified
    /// as a safe-to-retry <see cref="UploadBodyTransferException"/> exactly like
    /// <see cref="UploadMultipartAsync"/>: a presigned PUT commits nothing until the body finishes, so a
    /// connect-phase/mid-send fault is safe for the shared retry layer to re-run. (Whether the re-run is
    /// non-double-creating is the caller's concern — e.g. storage.to's uploaded object only becomes a
    /// downloadable file via a separate confirm step that a failed PUT never reaches.)
    /// </summary>
    public Task<HttpResponseSnapshot> UploadPutAsync(
        string filePath,
        string endpoint,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default)
        => UploadFileBodyAsync(HttpMethod.Put, filePath, endpoint, contentType, headers, getBytesPerSecond, cancellationToken);

    /// <summary>
    /// Streams a file as the raw request body via an arbitrary <paramref name="method"/> — the
    /// method-parameterized core behind <see cref="UploadPutAsync"/>. MediaFire's
    /// <c>upload/simple.php</c> wants a <b>POST</b> of the raw bytes (Content-Type
    /// <c>application/octet-stream</c>) with <c>x-filename</c>/<c>x-filesize</c>/<c>x-filehash</c>
    /// headers and the <c>session_token</c> in the query string; the server rejects a multipart body
    /// because it validates <c>x-filesize</c> against the exact body length. Progress via
    /// <see cref="UploadProgress"/>; a connect-phase/mid-send fault is reclassified as a retryable
    /// <see cref="UploadBodyTransferException"/> exactly as for a raw PUT.
    /// </summary>
    public async Task<HttpResponseSnapshot> UploadFileBodyAsync(
        HttpMethod method,
        string filePath,
        string endpoint,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;
        endpoint = MaybeRewriteToMockServer(endpoint);

        HttpTransaction transaction = new()
        {
            Method = method.Method,
            Url = endpoint,
            Proxy = _proxyDescription,
            StartTime = dateTimeStarted,
            RequestBody = $"[Raw {method.Method} file upload: {Path.GetFileName(filePath)}]",
        };

        // Null until the FileStream is opened + the content created — a fault while it's still null is a
        // local setup error (source file gone), NOT a network "nothing committed" case. See the matching
        // guard in UploadMultipartAsync.
        ProgressStreamContent? progressContent = null;

        try
        {
            FileStream rawStream = new(filePath, FileMode.Open, FileAccess.Read);
            Stream fileStream = getBytesPerSecond is not null
                ? new ThrottledStream(rawStream, getBytesPerSecond)
                : rawStream;
            using Stream disposeFileStream = fileStream;
            progressContent = new(
                fileStream,
                (totalBytes, bytesTransferred) => UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted)),
                cancellationToken);
            // Empty means "send no Content-Type" — see UploadBytesAsync, which needs the same and is
            // where it matters.
            if (!string.IsNullOrEmpty(contentType))
            {
                progressContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }

            // HttpRequestMessage.Dispose() disposes its Content for us — no separate using on the
            // content (that'd double-dispose). Mirrors UploadMultipartAsync.
            using HttpRequestMessage request = new(method, endpoint) { Content = progressContent };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, progressContent, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
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
            transaction.ResponseBody = ex.ToString();
            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));

            // Same reclassification as UploadMultipartAsync: until the body is fully sent the presigned
            // target committed nothing, so a transport fault is a safe-to-retry body-transfer abort.
            // Guard on progressContent being non-null so a pre-creation setup fault (file gone) stays a
            // plain terminal error.
            if (progressContent is { BodyFullySent: false } && !UploadBodyTransferException.IsInChain(ex))
            {
                throw new UploadBodyTransferException(ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Uploads an in-memory body via <paramref name="method"/> to <paramref name="url"/> with the given
    /// <c>Content-Type</c> + headers — used for a single Backblaze B2 blob (a ≤5 MB slice of the encrypted
    /// stream, POSTed to <c>b2_upload_file</c>). Progress via <see cref="UploadProgress"/>; a
    /// connect-phase/mid-send fault is reclassified as a retryable
    /// <see cref="UploadBodyTransferException"/> exactly like <see cref="UploadPutAsync"/> (a failed B2
    /// blob commits nothing — wormhole's file record is only made by the later finish-upload — so a
    /// whole-pipeline retry against a fresh room never double-creates).
    /// </summary>
    public async Task<HttpResponseSnapshot> UploadBytesAsync(
        HttpMethod method,
        string url,
        byte[] body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;
        url = MaybeRewriteToMockServer(url);

        HttpTransaction transaction = new()
        {
            Method = method.Method,
            Url = url,
            Proxy = _proxyDescription,
            StartTime = dateTimeStarted,
            RequestBody = $"[Raw {method.Method}: {body.Length} bytes]",
        };

        ProgressStreamContent? progressContent = null;
        try
        {
            MemoryStream rawStream = new(body, 0, body.Length, writable: false);
            Stream bodyStream = getBytesPerSecond is not null ? new ThrottledStream(rawStream, getBytesPerSecond) : rawStream;
            using Stream disposeBodyStream = bodyStream;
            progressContent = new(
                bodyStream,
                (totalBytes, bytesTransferred) => UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted)),
                cancellationToken);

            // An EMPTY contentType means "send none", which some signed APIs require: S3/R2 verify a
            // signature over exactly the headers the caller listed, and a browser's
            // CreateMultipartUpload sends no Content-Type at all. Forcing one on makes the request
            // differ from the shape known to work, and the old behaviour — throwing on empty — left
            // no way to express it.
            if (!string.IsNullOrEmpty(contentType))
            {
                progressContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }

            using HttpRequestMessage request = new(method, url) { Content = progressContent };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            CaptureRequestHeaders(transaction, progressContent, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = responseBody;
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(response.IsSuccessStatusCode, responseBody, dateTimeStarted));

            return new HttpResponseSnapshot((int)response.StatusCode, responseBody, ReadSetCookies(response), response.Headers.Location?.OriginalString);
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
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));

            if (progressContent is { BodyFullySent: false } && !UploadBodyTransferException.IsInChain(ex))
            {
                throw new UploadBodyTransferException(ex);
            }

            throw;
        }
    }

    /// <summary>
    /// PUTs a single chunk as a <em>raw octet-stream body</em> to <paramref name="endpoint"/> — the
    /// shape the XFileSharing "xfspro" upload plugin uses (<c>put_chunk.cgi</c>, captured from
    /// filehoster.io 2026-06-29): each chunk is a bare <c>PUT</c> carrying an <c>X-Upload-SID</c> header
    /// (passed via <paramref name="headers"/>), and the server appends chunks in order under that SID.
    /// Progress is file-cumulative via <paramref name="basePosition"/> (same translation as
    /// <see cref="PostChunkAsync"/>). The caller slices the file (see <see cref="ChunkSliceStream"/>).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PostChunkAsync"/> (the up.cgi protocol, which commits each chunk into the final
    /// file and therefore STRIPS the retry marker), xfspro chunks accumulate under a <em>disposable,
    /// client-chosen SID</em> and only the later <c>import_file</c> creates the file record. So a
    /// body-not-fully-sent fault is safe for the shared retry layer to re-run the WHOLE pipeline — which
    /// picks a FRESH SID, orphaning the partial upload and never double-creating. This is the
    /// <see cref="UploadPutAsync"/>/storage.to model, so the same <c>BodyFullySent</c> reclassification
    /// applies here.
    /// </remarks>
    /// <param name="method">Verb for the chunk. Defaults to PUT (the xfspro/R2 shape). DropMeFiles
    /// runs the same raw-body-plus-headers protocol over POST, so the verb is a parameter rather than
    /// a second near-identical method.</param>
    public async Task<HttpResponseSnapshot> PutChunkAsync(
        string endpoint,
        Stream chunkData,
        long chunkLength,
        long basePosition,
        long totalFileSize,
        DateTime dateTimeStarted,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<long?>? getBytesPerSecond = null,
        CancellationToken cancellationToken = default,
        HttpMethod? method = null)
    {
        endpoint = MaybeRewriteToMockServer(endpoint);

        HttpMethod verb = method ?? HttpMethod.Put;

        HttpTransaction transaction = new()
        {
            Method = verb.Method,
            Url = endpoint,
            Proxy = _proxyDescription,
            StartTime = dateTimeStarted,
            RequestBody = $"[{verb.Method} chunk @ {basePosition}: {chunkLength} bytes]",
        };

        ProgressStreamContent? progressContent = null;

        try
        {
            Stream chunkStream = getBytesPerSecond is not null
                ? new ThrottledStream(chunkData, getBytesPerSecond)
                : chunkData;
            progressContent = new(
                chunkStream,
                // Per-chunk → file-cumulative progress so the UI sees one monotonic stream.
                (_, bytesInThisChunk) => UploadProgress?.Invoke(
                    this,
                    new OperationProgressEventArgs(totalFileSize, basePosition + bytesInThisChunk, dateTimeStarted)),
                cancellationToken);
            progressContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using HttpRequestMessage request = new(verb, endpoint) { Content = progressContent };
            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    // Content-* headers belong on the CONTENT, and .NET refuses them on the request:
                    // TryAddWithoutValidation returns false and the header is silently dropped. A
                    // resumable-upload protocol keyed on Content-Range would then fail with nothing
                    // in the log to say why (DropMeFiles answers 415), so route them explicitly.
                    if (!request.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    {
                        progressContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                }
            }

            CaptureRequestHeaders(transaction, progressContent, requestHeaders: request.Headers);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            transaction.EndTime = DateTime.Now;
            transaction.StatusCode = (int)response.StatusCode;
            transaction.StatusReason = response.ReasonPhrase ?? response.StatusCode.ToString();
            transaction.ResponseBody = body;
            CaptureResponseHeaders(transaction, response);

            LogTransaction(transaction);

            // ETag surfaced for S3/R2 multipart part PUTs (storage.to's complete-multipart echoes each part's
            // ETag). Kept verbatim/quoted; fall back to the raw header if the typed accessor rejects the format.
            string? etag = response.Headers.ETag?.Tag
                ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? ev) ? ev.FirstOrDefault() : null);
            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response), response.Headers.Location?.OriginalString, etag);
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

            // xfspro chunks are whole-pipeline-retry-safe (fresh SID discards the partial), so reclassify
            // a body-not-fully-sent fault exactly like UploadPutAsync — do NOT strip the marker the way
            // PostChunkAsync (commit-per-chunk) must.
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
    /// <summary>
    /// POSTs one chunk of a file as <c>multipart/form-data</c> with caller-chosen field names — the
    /// generic sibling of <see cref="PostChunkAsync"/>, which is fixed to XFileSharing's
    /// <c>sid</c>/<c>file</c> shape. GigaFile is the first host to need this: its chunk carries
    /// <c>id</c>, <c>name</c>, <c>chunk</c>, <c>chunks</c> and <c>lifetime</c> beside the bytes.
    /// <para>
    /// Progress is reported file-cumulative (<paramref name="basePosition"/> + bytes sent in this
    /// chunk) so the UI sees one rising line across the whole file rather than one cycle per chunk.
    /// </para>
    /// </summary>
    /// <param name="fileFieldName">Form field the bytes go in.</param>
    /// <param name="filePartName">The <c>filename=</c> the part declares. Hosts differ on whether
    /// they read the real name from here or from a separate field; the caller decides.</param>
    public async Task<HttpResponseSnapshot> PostChunkMultipartAsync(
        string endpoint,
        Stream chunkData,
        long chunkLength,
        long basePosition,
        long totalFileSize,
        DateTime dateTimeStarted,
        string fileFieldName,
        string filePartName,
        IReadOnlyDictionary<string, string>? extraFields = null,
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
            RequestBody = $"[Chunk @ {basePosition}: {chunkLength} bytes]",
        };

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

            Stream chunkStream = getBytesPerSecond is not null
                ? new ThrottledStream(chunkData, getBytesPerSecond)
                : chunkData;
            ProgressStreamContent chunkPart = new(
                chunkStream,
                (_, bytesInThisChunk) => UploadProgress?.Invoke(
                    this,
                    new OperationProgressEventArgs(totalFileSize, basePosition + bytesInThisChunk, dateTimeStarted)),
                cancellationToken);

            chunkPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipartContent.Add(chunkPart, fileFieldName);

            // Content-Disposition BEFORE Content-Type on a file part, as browsers and curl send it —
            // see the note on BuildFilePartContentDisposition; one host accepted whole uploads and
            // then reported "no file found" without it.
            chunkPart.Headers.ContentDisposition = null;
            chunkPart.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                $"form-data; name=\"{fileFieldName}\"; filename=\"{filePartName}\"");

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
            throw;
        }
    }

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
            // the chain in a single pass.
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is UploadBodyTransferException marker)
                {
                    // Rethrow the underlying transport cause, never the marker itself — a chunk
                    // transport fault must never carry a retryable signal to AttemptRunner. The
                    // fallback is a plain (non-marker) exception so the strip can't re-expose the
                    // retryable type even if a marker ever lacked an inner.
                    throw marker.InnerException ?? new IOException(marker.Message);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// POSTs a chunk as browser-shaped <c>multipart/form-data</c> with caller-chosen string fields plus a
    /// file part carrying the <em>real</em> filename — the generalized cousin of <see cref="PostChunkAsync"/>
    /// (ufile.io's <c>/v1/upload/chunk</c>: fields <c>chunk_index</c>/<c>fuid</c> + a <c>file</c> part).
    /// Progress is file-cumulative via <paramref name="basePosition"/>. UNLIKE <see cref="PostChunkAsync"/>,
    /// a mid-send fault is reclassified as a retryable <see cref="UploadBodyTransferException"/> (like
    /// <see cref="UploadPutAsync"/>): callers of this method create a fresh upload session per attempt, so a
    /// whole-pipeline retry re-uploads under a new id and never double-commits (nothing is committed until a
    /// separate finalise call). The caller slices the file (see <see cref="ChunkSliceStream"/>).
    /// </summary>
    public async Task<HttpResponseSnapshot> PostFileChunkAsync(
        string endpoint,
        IReadOnlyDictionary<string, string> fields,
        string fileFieldName,
        string fileName,
        Stream chunkData,
        long chunkLength,
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
            RequestBody = $"[File chunk: {chunkLength} bytes, {fileName}]",
        };

        // Null until the ProgressStreamContent is created — a fault before then is a setup error, not a
        // mid-send body abort (mirrors UploadPutAsync's guard).
        ProgressStreamContent? chunkPart = null;

        try
        {
            using MultipartFormDataContent multipartContent = BuildBrowserShapedMultipart(out string _);
            foreach (KeyValuePair<string, string> field in fields)
            {
                AddBareStringPart(multipartContent, field.Key, field.Value);
            }

            Stream chunkStream = getBytesPerSecond is not null
                ? new ThrottledStream(chunkData, getBytesPerSecond)
                : chunkData;
            chunkPart = new(
                chunkStream,
                (_, bytesInThisChunk) => UploadProgress?.Invoke(
                    this,
                    new OperationProgressEventArgs(totalFileSize, basePosition + bytesInThisChunk, dateTimeStarted)),
                cancellationToken);
            multipartContent.Add(chunkPart, fileFieldName);
            chunkPart.Headers.ContentDisposition = null;

            // Content-Disposition first, then Content-Type — same browser-shaped ordering (and the
            // same reason) as AddFilePart; see the note there.
            chunkPart.Headers.TryAddWithoutValidation("Content-Disposition", BuildFilePartContentDisposition(fileFieldName, fileName));
            chunkPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

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

            if (chunkPart is { BodyFullySent: false } && !UploadBodyTransferException.IsInChain(ex))
            {
                throw new UploadBodyTransferException(ex);
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
        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

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
        MultipartFormDataContent content = [with(boundary)];

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
    /// Attaches the file part with a <em>browser-shaped</em> <c>Content-Disposition</c> (see
    /// <see cref="BuildFilePartContentDisposition"/> for the filename encoding), and stamps the part
    /// with a real MIME type guessed from the extension instead of the generic
    /// <c>application/octet-stream</c>.
    /// </summary>
    internal static void AddFilePart(MultipartFormDataContent multipart, HttpContent fileContent, string fieldName, string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        // Add first with the (content, name) overload so .NET sets a baseline
        // Content-Disposition; then overwrite it with our cleaner version.
        multipart.Add(fileContent, fieldName);
        fileContent.Headers.ContentDisposition = null;

        // ORDER IS LOAD-BEARING: Content-Disposition must be the part's FIRST header, because that
        // is what every browser and curl emit and some servers parse positionally rather than by
        // name. 1fichier.com's upload.cgi is one — with Content-Type first it accepts the whole body
        // and answers "Pas de fichier trouvé dans l'envoi" ("no file found in the upload"), a silent
        // 200 that costs the entire transfer (isolated live 2026-07-29: same bytes, same field name,
        // only the two headers swapped — Content-Type first 200s, Content-Disposition first 302s).
        // Headers serialise in insertion order, so add the disposition BEFORE the content type.
        fileContent.Headers.TryAddWithoutValidation("Content-Disposition", BuildFilePartContentDisposition(fieldName, fileName));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeGuesser.Guess(filePath));
    }

    /// <summary>
    /// Builds the file part's <c>Content-Disposition</c> exactly as a browser does for a multipart
    /// upload: the filename in a quoted <c>filename="…"</c> carrying its RAW UTF-8 BYTES, with no
    /// RFC 5987 <c>filename*</c> (browsers omit it here, and the duplicate trips some Perl parsers).
    /// </summary>
    /// <remarks>
    /// .NET serializes multipart part headers with Latin-1 (ISO-8859-1), which would replace every
    /// non-ASCII character of the name with <c>?</c> — the "?????" filenames hosters stored for, e.g.,
    /// Japanese names. So for a non-ASCII name we re-encode it as its UTF-8 bytes reinterpreted as
    /// Latin-1 characters: .NET's Latin-1 wire serialization then emits the original UTF-8 bytes,
    /// byte-identical to a real browser upload. ASCII names pass through unchanged.
    /// </remarks>
    internal static string BuildFilePartContentDisposition(string fieldName, string fileName)
    {
        string headerFileName = Ascii.IsValid(fileName)
            ? fileName
            : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(fileName));
        return $"form-data; name=\"{fieldName}\"; filename=\"{headerFileName}\"";
    }

    private void LogTransaction(HttpTransaction transaction) => logger.Log(null, LogType.Http, transaction.Summary, httpTransaction: transaction);

    private void CaptureRequestHeaders(HttpTransaction transaction, HttpContent? content, HttpRequestHeaders? requestHeaders = null)
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
