// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http.Headers;

using CSUploader.Lib;
using CSUploader.Lib.Net;

namespace CSUploader.Lib.Net.Http;

public class HttpHandler
{
    private readonly IAppLogger _logger;

    public HttpHandler(HttpClient httpclient, IAppLogger logger)
    {
        HttpClient = httpclient;
        _logger = logger;
    }

    public event EventHandler<OperationProgressEventArgs>? UploadProgress;

    public event EventHandler<ProtocolUploadFinishedEventArgs>? UploadFinished;

    protected HttpClient HttpClient { get; set; }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HttpTransaction transaction = new()
        {
            Method = "GET",
            Url = url,
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

    public async Task UploadFileAsync(string filePath, string endpoint, CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;

        HttpTransaction transaction = new()
        {
            Method = "POST",
            Url = endpoint,
            StartTime = dateTimeStarted,
            RequestBody = $"[Multipart file upload: {Path.GetFileName(filePath)}]",
        };

        try
        {
            using var multipartContent = new MultipartFormDataContent($"---------------------{DateTime.Now.Ticks:x}");
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var progressContent = new ProgressStreamContent(fileStream, (totalBytes, bytesTransferred) =>
            {
                UploadProgress?.Invoke(this, new OperationProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted));
            }, cancellationToken);

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
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, string.Empty, dateTimeStarted));
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

    private void LogTransaction(HttpTransaction transaction)
    {
        _logger.Log(null, LogType.Http, transaction.Summary, httpTransaction: transaction);
    }

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
