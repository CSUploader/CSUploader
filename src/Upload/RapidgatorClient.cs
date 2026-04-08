// <copyright file="RapidgatorClient.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CSUploader.Upload.Rapidgator;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Lib.Extensions;

namespace CSUploader.Upload;

/// <summary>
/// Rapidgator file host client.
/// </summary>
public class RapidgatorClient : FileHosterClient
{
    /// <summary>
    /// The hostname of the file hoster.
    /// </summary>
    private static readonly string Hostname = "rapidgator.net";

    private readonly IAppLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RapidgatorClient"/> class.
    /// </summary>
    /// <param name="protocol">The protocol.</param>
    /// <param name="logger">The application logger.</param>
    /// <exception cref="ArgumentOutOfRangeException">protocol - Protocol not supported.</exception>
    public RapidgatorClient(Protocol protocol, IAppLogger logger)
        : base(protocol)
    {
        _logger = logger;
        if (protocol == Protocol.Http)
        {
            HttpClientHandler httpClientHandler = new()
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                //httpClientHandler.Proxy = new WebProxy("127.0.0.1:8888");
                UseProxy = true
            };
            HttpClient httpClient = new(httpClientHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Always en-US; no language differences
            httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));

            // Set user agent; some sites (Subyshare) don't like the default one
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:62.0) Gecko/20100101 Firefox/62.0");

            Cookie languageCookie = new("lang", "en", "/", "www.rapidgator.com");
            httpClientHandler.CookieContainer.Add(languageCookie);

            HttpHandler = new HttpHandler(httpClient, _logger);
        }
        else if (protocol == Protocol.Ftp)
        {
            throw new NotImplementedException();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), "Protocol not supported");
        }

        HttpHandler.UploadProgress += HttpHandler_UploadProgress;
        HttpHandler.UploadFinished += HttpHandler_UploadFinished;
    }

    /// <summary>
    /// Gets the name of the file hoster.
    /// </summary>
    /// <value>
    /// The name of the file hoster.
    /// </value>
    public override string Name { get; } = "Rapidgator";

    /// <summary>
    /// Gets a value indicating whether [requires hashing before upload].
    /// </summary>
    /// <value>
    ///   <c>true</c> if [requires hashing before upload]; otherwise, <c>false</c>.
    /// </value>
    public override bool RequiresHashingBeforeUpload => string.IsNullOrEmpty(FileHash);

    private UserInfoResponse? UserInfoResponse { get; set; }

    private HttpHandler HttpHandler { get; set; }

    private string? FileHash { get; set; }

    /// <summary>
    /// Hash a file asynchronously.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="pauseToken">The pause token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous operation.</returns>
    public override async Task HashAsync(string filePath, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
    {
        // Calculate MD5
        using MD5 md5 = MD5.Create();
        using FileStream fs = File.OpenRead(filePath);
        byte[] hash = await Hashing.ComputeHashAsync(md5, fs, pauseToken, cancellationToken);

        FileHash = string.Join(string.Empty, hash.Select(s => s.ToString("x2")).ToArray());
    }

    /// <inheritdoc/>
    public override Task UploadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override async Task UploadAsync(string filePath, string username, string password, CancellationToken cancellationToken = default)
    {
        if (Protocol == Protocol.Http)
        {
            if (UserInfoResponse?.User == null || !string.Equals(UserInfoResponse.User.Email, username, StringComparison.OrdinalIgnoreCase))
            {
                UserInfoResponse = await HttpLoginAsync(username, password, cancellationToken);
                if (UserInfoResponse == null)
                {
                    UploadFinishedCallback("Failed to login");
                    return;
                }
            }

            // Create folder using the parent directory name
            string folderName = Path.GetDirectoryName(filePath) is string dir ? new DirectoryInfo(dir).Name : "uploads";
            FolderCreateResponse? folderCreateResponse = await HttpCreateFolderAsync(folderName, UserInfoResponse, cancellationToken);
            if (folderCreateResponse == null)
            {
                UploadFinishedCallback("Failed to create folder");
                return;
            }

            // Upload file to folder
            await HttpUploadFileAsync(UserInfoResponse, folderCreateResponse, filePath, cancellationToken);
        }
        else if (Protocol == Protocol.Ftp)
        {
        }
    }

    private Task<UserInfoResponse?> HttpLoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        string link = $"http://{Hostname}/api/v2/user/login?login={username}&password={password}";
        return HttpSendReceiveAsync<UserInfoResponse>(link, "login", cancellationToken);
    }

    private Task<FolderCreateResponse?> HttpCreateFolderAsync(string folderName, UserInfoResponse userInfoResponse, CancellationToken cancellationToken = default)
    {
        string link = $"http://{Hostname}/api/v2/folder/create?name={folderName}&token={userInfoResponse.Token}";
        return HttpSendReceiveAsync<FolderCreateResponse>(link, "create folder", cancellationToken);
    }

    private async Task HttpUploadFileAsync(UserInfoResponse userInfoResponse, FolderCreateResponse folderCreateResponse, string filePath, CancellationToken cancellationToken = default)
    {
        FileUploadResponse? fileUploadRequestResponse = await HttpUploadFileRequestAsync(userInfoResponse, folderCreateResponse, filePath, cancellationToken);
        if (fileUploadRequestResponse == null)
        {
            UploadFinishedCallback("Failed to get file upload request");
            return;
        }
        
        if (fileUploadRequestResponse.Upload == null || string.IsNullOrEmpty(fileUploadRequestResponse.Upload.Url))
        {
            UploadFinishedCallback("Failed to get file upload link");
            return;
        }

        string link = fileUploadRequestResponse.Upload.Url;

        await HttpHandler.UploadFileAsync(filePath, link, cancellationToken);
    }

    private async Task<FileUploadResponse?> HttpUploadFileRequestAsync(UserInfoResponse userInfoResponse, FolderCreateResponse folderCreateResponse, string filePath, CancellationToken cancellationToken = default)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException("File does not exist.");
        }

        if (folderCreateResponse.Folder == null)
        {
            throw new ArgumentOutOfRangeException(nameof(FolderCreateResponse), "Folder does not exist.");
        }

        string fileName = Path.GetFileName(filePath);
        string link = $"http://{Hostname}/api/v2/file/upload?folder_id={folderCreateResponse.Folder.Id}&name={fileName}&hash={FileHash}&size={fileInfo.Length}&token={userInfoResponse.Token}";

        return await HttpSendReceiveAsync<FileUploadResponse>(link, "upload file", cancellationToken);
    }

    private async Task<T?> HttpSendReceiveAsync<T>(string link, string action, CancellationToken cancellationToken = default)
        where T : class
    {
        string? result = null;
        try
        {
            result = await HttpHandler.GetStringAsync(link, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to send HTTP GET: {ex}");
            throw;
        }

        if (result == null)
        {
            return null;
        }

        if (JsonHelpers.TryDeserializeObject(result, out Response<T>? modelResponse))
        {
            if (modelResponse?.Status != 200 || !string.IsNullOrEmpty(modelResponse.Details))
            {
                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to {action}: {modelResponse?.Status} {modelResponse?.Details}");
                return null;
            }

            return modelResponse.Model;
        }
        else if (JsonHelpers.TryDeserializeObject(result, out Response? response))
        {
            _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to {action}:{Environment.NewLine}"
                + $"Response Status: {response?.Status}{Environment.NewLine}"
                + $"Response details: {response?.Details}{Environment.NewLine}");
        }
        else
        {
            _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Action '{action}' failed to execute: {result}");
        }

        return null;
    }

    private void HttpHandler_UploadProgress(object? sender, OperationProgressEventArgs e)
    {
        OperationProgressEventArgs eventArgs = new(e.Size, e.BytesProcessed, e.DateTimeStarted);

        FireUploadProgress(this, eventArgs);
    }

    private void HttpHandler_UploadFinished(object? sender, ProtocolUploadFinishedEventArgs e)
    {
        FileHosterUploadFinishedEventArgs eventArgs = new(e.Success, e.Result ?? string.Empty, e.DateTimeFinished);
        DateTime startDateTime = e.DateTimeFinished - e.TimeElapsed;

        if (string.IsNullOrEmpty(e.Result))
        {
            return;
        }

        if (JsonHelpers.TryDeserializeObject(e.Result, out Response<FileUploadResponse>? fileUploadResponse) && !string.IsNullOrEmpty(fileUploadResponse.Details))
        {
            if (fileUploadResponse.Status != 200)
            {
                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to upload file: {fileUploadResponse.Status} {fileUploadResponse.Details}");
                eventArgs = new FileHosterUploadFinishedEventArgs(false, fileUploadResponse.Details, startDateTime);
            }
            else if (fileUploadResponse.Model?.Upload?.File is { Length: > 0 })
            {
                FileFile file = fileUploadResponse.Model.Upload.File[0];
                FileHosterFileStatus fileStatus = file.Mode == 0
                    ? FileHosterFileStatus.Free
                    : file.Mode == 1
                        ? FileHosterFileStatus.PremiumOnly
                        : file.Mode == 2
                            ? FileHosterFileStatus.Private
                            : FileHosterFileStatus.Hotlink;
                FileHosterFileInfo fileInfo = new()
                {
                    Id = file.FileId,
                    FileStatus = fileStatus,
                    FileName = file.Name,
                    Checksum = file.Hash,
                    Url = file.Url
                };

                eventArgs = new FileHosterUploadFinishedEventArgs(true, fileUploadResponse.Details, startDateTime, fileInfo);
            }
        }
        else if (JsonHelpers.TryDeserializeObject(e.Result, out Response? response) && !string.IsNullOrEmpty(response.Details))
        {
            _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to upload file: {response.Status} {response.Details}");
            eventArgs = new FileHosterUploadFinishedEventArgs(false, response.Details, startDateTime);
        }

        UploadFinishedCallback(eventArgs);
    }

    private void UploadFinishedCallback(string errorMessage)
    {
        UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(false, errorMessage, DateTime.Now));
    }

    private void UploadFinishedCallback(FileHosterUploadFinishedEventArgs e)
    {
        FireUploadFinished(this, e);
    }
}
