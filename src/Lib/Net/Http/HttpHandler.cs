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

        _logger.Log(null, LogType.Http, $"GET {url} HTTP/1.1");

        try
        {
            using HttpResponseMessage responseMessage = await HttpClient.GetAsync(url, cancellationToken);
            string result = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
            _logger.Log(null, LogType.Http, result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.Log(null, LogType.Error, $"Failed to send GET request to `{url}`: {ex.Message}{Environment.NewLine}{ex}");
            throw;
        }
    }

    public async Task UploadFileAsync(string filePath, string endpoint, CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;

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

            using HttpResponseMessage response = await HttpClient.PostAsync(endpoint, multipartContent, cancellationToken);
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync(cancellationToken);

            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(true, result, dateTimeStarted));
        }
        catch (OperationCanceledException)
        {
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, string.Empty, dateTimeStarted));
        }
        catch (HttpRequestException ex)
        {
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
        catch (Exception ex)
        {
            UploadFinished?.Invoke(this, new ProtocolUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
    }
}
