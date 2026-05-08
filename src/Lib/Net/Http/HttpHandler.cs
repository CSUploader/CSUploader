// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http.Headers;
using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

public class HttpHandler
{
    private readonly IAppLogger _logger;
    private readonly string _proxyDescription;
    private readonly bool _bypassMockServer;
    private readonly MockServerConfig _mockServer;

    /// <summary>
    /// Legacy ctor — reads <see cref="AppSettings.Current"/> for the mock snapshot.
    /// New code should pass an explicit <see cref="MockServerConfig"/> instead. Kept
    /// during the pipeline migration; deleted in Phase 4.
    /// </summary>
    public HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription = null, bool bypassMockServer = false)
        : this(httpclient, logger, proxyDescription, MockServerConfig.FromAppSettings(AppSettings.Current), bypassMockServer)
    {
    }

    public HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription, MockServerConfig mockServer, bool bypassMockServer = false)
    {
        HttpClient = httpclient;
        _logger = logger;
        _proxyDescription = string.IsNullOrEmpty(proxyDescription) ? "(direct)" : proxyDescription;
        _bypassMockServer = bypassMockServer;
        _mockServer = mockServer;
    }

    /// <summary>Test-observable snapshot of the mock config locked in at construction.</summary>
    internal MockServerConfig MockServerSnapshot => _mockServer;

    private string MaybeRewriteToMockServer(string url)
    {
        if (_bypassMockServer)
        {
            // Caller (e.g. proxy connectivity test) explicitly opted out of the dev
            // redirect. Don't even log the "mock disabled" line — that's only useful
            // for upload traffic.
            return url;
        }

        if (!_mockServer.Enabled || string.IsNullOrEmpty(_mockServer.BaseUrl))
        {
            _logger.Log(this, LogType.Status, $"Mock server disabled — sending to live URL: {url}");
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? originalUri))
        {
            return url;
        }

        if (!Uri.TryCreate(_mockServer.BaseUrl, UriKind.Absolute, out Uri? mockUri))
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

        string mockBase = _mockServer.BaseUrl.TrimEnd('/');
        string rewritten = $"{mockBase}/{slug}{originalUri.PathAndQuery}";
        _logger.Log(this, LogType.Status, $"Mock rewrite: {url} -> {rewritten}");
        return rewritten;
    }

    public event EventHandler<OperationProgressEventArgs>? UploadProgress;

    public event EventHandler<ProtocolUploadFinishedEventArgs>? UploadFinished;

    protected HttpClient HttpClient { get; }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
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
            // Capture request headers
            CaptureRequestHeaders(transaction, null);

            using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
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
            using var multipartContent = new MultipartFormDataContent($"---------------------{DateTime.Now.Ticks:x}");
            FileStream rawStream = new(filePath, FileMode.Open, FileAccess.Read);
            Stream fileStream = getBytesPerSecond is not null
                ? new ThrottledStream(rawStream, getBytesPerSecond)
                : rawStream;
            using var disposeFileStream = fileStream;
            var progressContent = new ProgressStreamContent(fileStream, (totalBytes, bytesTransferred) => UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted)), cancellationToken);

            progressContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipartContent.Add(progressContent, "file", Path.GetFileName(filePath));

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
            transaction.ResponseBody = ex.Message;
            LogTransaction(transaction);
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
    }

    private void LogTransaction(HttpTransaction transaction) => _logger.Log(null, LogType.Http, transaction.Summary, httpTransaction: transaction);

    private void CaptureRequestHeaders(HttpTransaction transaction, HttpContent? content)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in HttpClient.DefaultRequestHeaders)
        {
            transaction.RequestHeaders[header.Key] = [.. header.Value];
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
