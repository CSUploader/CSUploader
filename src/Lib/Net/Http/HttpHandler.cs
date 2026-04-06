// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System;

namespace CSUploader.Lib.Net.Http;

public class HttpHandler : IProtocolHandler
{
    public HttpHandler()
    {
        HttpClient = new HttpClient();
    }

    public HttpHandler(HttpClient httpclient)
    {
        HttpClient = httpclient;
    }

    public HttpHandler(HttpMessageHandler messageHandler)
    {
        HttpClient = new HttpClient(messageHandler);
    }

    public HttpHandler(HttpClientHandler clientHandler)
    {
        HttpClient = new HttpClient(clientHandler);
    }

    public event EventHandler<HttpUploadProgressEventArgs>? UploadProgress;

    public event EventHandler<HttpUploadFinishedEventArgs>? UploadFinished;

    protected HttpClient HttpClient { get; set; }

    public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return GetStringAsync(uri.AbsoluteUri, cancellationToken);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Logger.Current.Log(null, LogType.Http, $"GET {url} HTTP/1.1");

        try
        {
            HttpResponseMessage responseMessage = await HttpClient.GetAsync(url, cancellationToken);
            string result = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
            Logger.Current.Log(null, LogType.Http, result);

            return result;
        }
        catch (Exception ex)
        {
            Logger.Current.Log(null, LogType.Error, $"Failed to send GET request to `{url}`: {ex.Message}{Environment.NewLine}{ex}");
            throw;
        }
    }

    public static Task<byte[]> GetBytesAsync(HttpClient client, Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return GetBytesAsync(client, uri.AbsoluteUri, cancellationToken);
    }

    public static async Task<byte[]> GetBytesAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Logger.Current.Log(null, LogType.Http, $"GET {url} HTTP/1.1");

        try
        {
            byte[] bytes = await client.GetByteArrayAsync(url, cancellationToken);
            // Logger.Current.Log(null, LogType.Http, $"Received {bytes?.Length}");

            return bytes;
        }
        catch (Exception ex)
        {
            Logger.Current.Log(null, LogType.Error, $"Failed to send GET request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
            return [];
        }
    }

    public Task<string?> PostAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return PostAsync(uri.AbsoluteUri, cancellationToken);
    }

    public Task<string?> PostAsync(string url, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> values = [];

        return PostAsync(url, values, cancellationToken);
    }

    public Task<string?> PostAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return PostAsync(uri.AbsoluteUri, values, cancellationToken);
    }

    public Task<string?> PostAsync(string url, IEnumerable<KeyValuePair<string, string>> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FormUrlEncodedContent content = new(values);
        string keyValues = values.Aggregate(string.Empty, (v, k) => v += $"{k.Key}={k.Value}" + Environment.NewLine);
        Logger.Current.Log(null, LogType.Http, $"POST {url} HTTP/1.1" + Environment.NewLine + $"{keyValues}");

        return PostAsync(url, content, cancellationToken);
    }

    public async Task<string?> PostAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await HttpClient.PostAsync(url, content, cancellationToken);
            string result = await response.Content.ReadAsStringAsync();
            Logger.Current.Log(null, LogType.Http, result);

            return result;
        }
        catch (Exception ex)
        {
            Logger.Current.Log(null, LogType.Error, $"Failed to send POST request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
            return null;
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
                int percentage = (int)Math.Floor((100.0 / totalBytes) * bytesTransferred);
                UploadProgress?.Invoke(this, new HttpUploadProgressEventArgs(totalBytes, bytesTransferred, dateTimeStarted));
            }, cancellationToken);

            progressContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipartContent.Add(progressContent, "file", Path.GetFileName(filePath));

            using var response = await HttpClient.PostAsync(endpoint, multipartContent, cancellationToken);
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(true, result, dateTimeStarted));
        }
        catch (OperationCanceledException)
        {
            UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, string.Empty, dateTimeStarted));
        }
        catch (HttpRequestException ex)
        {
            UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
        catch (Exception ex)
        {
            UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, ex.Message, dateTimeStarted));
        }
    }

    public Task<DownloadLinkInfo?> GetDownloadLinkInfoAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return GetDownloadLinkInfoAsync(uri.AbsoluteUri, cancellationToken);
    }

    public async Task<DownloadLinkInfo?> GetDownloadLinkInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Logger.Current.Log(null, LogType.Http, $"GET {url} HTTP/1.1");
        using HttpRequestMessage httpRequestMessage = new(HttpMethod.Get, url);
        try
        {
            using HttpResponseMessage httpResponseMessage = await HttpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            switch (httpResponseMessage.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;

                case HttpStatusCode.NotFound:
                    {
                        string content = await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken);
                        // Logger.Current.Log(null, LogType.Error, $"HTTP 404 {url} - {content}");
                        if (string.Equals("HTTP/1.1 404 Not Found", content, StringComparison.OrdinalIgnoreCase))
                        {
                            // This is the right link, but the host failed to generate the link on the server
                            return new DownloadLinkInfo
                            {
                                FileName = null,
                                FileSize = -1
                            };
                        }

                        return null;
                    }

                default:
                    Logger.Current.Log(null, LogType.Error, $"HTTP response message status code {httpResponseMessage.StatusCode} is not OK for URL {url}");
                    return null;
            }

            if (httpResponseMessage.Content == null)
            {
                Logger.Current.Log(null, LogType.Error, $"HTTP response message content is null for URL {url}");
                return null;
            }

            if (httpResponseMessage.Content.Headers == null)
            {
                Logger.Current.Log(null, LogType.Error, $"HTTP response message content headers is null for URL {url}");
                return null;
            }

            HttpContentHeaders httpContentHeaders = httpResponseMessage.Content.Headers;

            // "httpContentHeads.ContentDisposition" header throws or is null... So we need to parse manually
            if (!httpContentHeaders.Contains("Content-Disposition"))
            {
                //Logger.Current.Log(null, LogType.Error, $"HTTP header does not contain a content-disposition for URL {url}");
                return null;
            }

            string value = httpContentHeaders.GetValues("Content-Disposition").First();
            if (string.IsNullOrEmpty(value))
            {
                Logger.Current.Log(null, LogType.Error, $"Content disposition header value is null or empty for URL {url}");
                return null;
            }

            ContentDisposition contentDisposition = new(value);
            if (string.IsNullOrEmpty(contentDisposition.FileName))
            {
                Logger.Current.Log(null, LogType.Error, $"Filename is empty in HTTP content disposition header for URL {url}");
                return null;
            }

            if (!httpContentHeaders.ContentLength.HasValue)
            {
                Logger.Current.Log(null, LogType.Error, $"Http header does not have content length for URL {url}");
                return null;
            }

            string fileName = contentDisposition.FileName;
            long fileSize = httpContentHeaders.ContentLength.Value;

            return new DownloadLinkInfo
            {
                FileName = fileName,
                FileSize = fileSize
            };
        }
        catch (Exception ex)
        {
            Logger.Current.Log(null, LogType.Error, $"Failed to send HEAD request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
            return null;
        }
    }
}
