// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http.Headers;

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

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response));
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
            ProgressStreamContent progressContent = new(
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

            return new HttpResponseSnapshot((int)response.StatusCode, body, ReadSetCookies(response));
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
            throw;
        }
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
