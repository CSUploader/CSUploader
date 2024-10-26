// <copyright file="HttpHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System;

namespace CSUploader.Lib.Net.Http
{
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

            Logger.Log(null, LogType.Http, $"GET {url} HTTP/1.1");

            try
            {
                HttpResponseMessage responseMessage = await HttpClient.GetAsync(url, cancellationToken);
                string result = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
                Logger.Log(null, LogType.Http, result);

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log(null, LogType.Error, $"Failed to send GET request to `{url}`: {ex.Message}{Environment.NewLine}{ex}");
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

            // Logger.Log(null, LogType.Http, $"GET {url} HTTP/1.1");

            try
            {
                byte[] bytes = await client.GetByteArrayAsync(url, cancellationToken);
                // Logger.Log(null, LogType.Http, $"Received {bytes?.Length}");

                return bytes;
            }
            catch (Exception ex)
            {
                Logger.Log(null, LogType.Error, $"Failed to send GET request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
                return Array.Empty<byte>();
            }
        }

        public Task<string?> PostAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return PostAsync(uri.AbsoluteUri, cancellationToken);
        }

        public Task<string?> PostAsync(string url, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new();

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
            Logger.Log(null, LogType.Http, $"POST {url} HTTP/1.1" + Environment.NewLine + $"{keyValues}");

            return PostAsync(url, content, cancellationToken);
        }

        public async Task<string?> PostAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpResponseMessage response = await HttpClient.PostAsync(url, content, cancellationToken);
                string result = await response.Content.ReadAsStringAsync();
                Logger.Log(null, LogType.Http, result);

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log(null, LogType.Error, $"Failed to send POST request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
                return null;
            }
        }

        public async Task UploadFileAsync(string filePath, string endpoint, CancellationToken cancellationToken = default)
        {
            DateTime dateTimeStarted = DateTime.Now;

            string boundary = "---------------------" + DateTime.Now.Ticks.ToString("x");
#pragma warning disable SYSLIB0014 // Type or member is obsolete
            if (WebRequest.Create(endpoint) is not HttpWebRequest webrequest)
            {
                return;
            }
#pragma warning restore SYSLIB0014 // Type or member is obsolete

            //webrequest.CookieContainer = cookies;
            webrequest.ContentType = "multipart/form-data; boundary=" + boundary;
            webrequest.Method = "POST";

            // Build up the post message header
            byte[] postHeaderBytes = Encoding.UTF8.GetBytes($@"
--{boundary}
Content-Disposition: form-data; name=""file""; filename=""{Path.GetFileName(filePath)}""
Content-Type: application/octet-stream

");

            // Build the trailing boundary string as a byte array
            // ensuring the boundary appears on a line by itself
            byte[] boundaryEndBytes = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
            FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read);
            long length = postHeaderBytes.Length + fileStream.Length + boundaryEndBytes.Length;
            webrequest.ContentLength = length;
            webrequest.AllowWriteStreamBuffering = false;
            webrequest.KeepAlive = false;

            using Stream requestStream = webrequest.GetRequestStream();
            try
            {
                // Write out our post header
                await requestStream.WriteAsync(postHeaderBytes, cancellationToken);

                // Write out the file contents; use a 1MB buffer
                // TODO: Make this buffer size a setting
                byte[] buffer = new byte[Math.Min(1048576, (int)fileStream.Length)];
                long totalBytesRead = 0;
                int percentageUploaded = 0;
                int bytesRead = 0;
                while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    totalBytesRead += bytesRead;

                    int percentage = (int)Math.Floor((100.0 / fileStream.Length) * totalBytesRead);
                    if (percentage > percentageUploaded)
                    {
                        percentageUploaded = percentage;

                        UploadProgress?.Invoke(this, new HttpUploadProgressEventArgs(fileStream.Length, totalBytesRead, dateTimeStarted));
                    }

                    await requestStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                // Write out the trailing boundary
                await requestStream.WriteAsync(boundaryEndBytes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, string.Empty, dateTimeStarted));
                return;
            }

            try
            {
                using WebResponse response = webrequest.GetResponse();
                using Stream s = response.GetResponseStream();
                using StreamReader sr = new(s);
                string result = sr.ReadToEnd();
                UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(true, result, dateTimeStarted));
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using Stream responseStream = wex.Response.GetResponseStream();
                    using StreamReader sr = new(responseStream);
                    string error = sr.ReadToEnd();

                    UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, error, dateTimeStarted));
                }
                else
                {
                    string error = wex.Message;

                    UploadFinished?.Invoke(this, new HttpUploadFinishedEventArgs(false, error, dateTimeStarted));
                }
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

            // Logger.Log(null, LogType.Http, $"GET {url} HTTP/1.1");
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
                            // Logger.Log(null, LogType.Error, $"HTTP 404 {url} - {content}");
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
                        Logger.Log(null, LogType.Error, $"HTTP response message status code {httpResponseMessage.StatusCode} is not OK for URL {url}");
                        return null;
                }

                if (httpResponseMessage.Content == null)
                {
                    Logger.Log(null, LogType.Error, $"HTTP response message content is null for URL {url}");
                    return null;
                }

                if (httpResponseMessage.Content.Headers == null)
                {
                    Logger.Log(null, LogType.Error, $"HTTP response message content headers is null for URL {url}");
                    return null;
                }

                HttpContentHeaders httpContentHeaders = httpResponseMessage.Content.Headers;

                // "httpContentHeads.ContentDisposition" header throws or is null... So we need to parse manually
                if (!httpContentHeaders.Contains("Content-Disposition"))
                {
                    //Logger.Log(null, LogType.Error, $"HTTP header does not contain a content-disposition for URL {url}");
                    return null;
                }

                string value = httpContentHeaders.GetValues("Content-Disposition").First();
                if (string.IsNullOrEmpty(value))
                {
                    Logger.Log(null, LogType.Error, $"Content disposition header value is null or empty for URL {url}");
                    return null;
                }

                ContentDisposition contentDisposition = new(value);
                if (string.IsNullOrEmpty(contentDisposition.FileName))
                {
                    Logger.Log(null, LogType.Error, $"Filename is empty in HTTP content disposition header for URL {url}");
                    return null;
                }

                if (!httpContentHeaders.ContentLength.HasValue)
                {
                    Logger.Log(null, LogType.Error, $"Http header does not have content length for URL {url}");
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
                Logger.Log(null, LogType.Error, $"Failed to send HEAD request to `{url}`: {ex.Message}" + Environment.NewLine + ex.ToString());
                return null;
            }
        }
    }
}
