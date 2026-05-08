// <copyright file="RapidgatorClient.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CSUploader.Dal;
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
    private const string Hostname = "rapidgator.net";

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
        if (protocol == Protocol.Ftp)
        {
            throw new NotImplementedException();
        }

        if (protocol is not Protocol.Http and not Protocol.Ftp)
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), "Protocol not supported");
        }

        // HttpHandler is built lazily at the start of each public HTTP-using call
        // (CheckAccountAsync, UploadAsync) so the proxy choice always reflects the
        // current ProxyManager state — not the state at queue-time. Changes to the
        // master "Use proxies for uploads" toggle (or the rotation list) take effect
        // on the next upload attempt automatically; no explicit refresh hook needed.
    }

    /// <summary>
    /// Tears down the previous <see cref="HttpHandler"/> (if any) and builds a fresh one
    /// against the current proxy rotation. Detaches old event handlers and clears the
    /// cached login response so credentials flow back through the new proxy. <c>internal</c>
    /// so tests can exercise the rotation behaviour without going through a real upload.
    /// </summary>
    internal void PrepareHttpHandler()
    {
        if (Protocol != Protocol.Http)
        {
            return;
        }

        if (HttpHandler is not null)
        {
            HttpHandler.UploadProgress -= HttpHandler_UploadProgress;
            HttpHandler.UploadFinished -= HttpHandler_UploadFinished;
        }

        // Forget the cached login response — credentials need to flow back through the
        // (potentially different) proxy on the next request.
        UserInfoResponse = null;

        HttpHandler = BuildHttpHandler();
        HttpHandler.UploadProgress += HttpHandler_UploadProgress;
        HttpHandler.UploadFinished += HttpHandler_UploadFinished;
    }

    private HttpHandler BuildHttpHandler()
    {
        // Pull a proxy from the rotation (null = direct connection). Resolved once
        // per client instance; new uploads pick a fresh proxy.
        ProxySettingDto? proxySetting = ProxyManager.Current?.NextProxy();
        IWebProxy? proxy = proxySetting is not null
            ? ProxyManager.BuildWebProxy(proxySetting)
            : null;
        ActiveProxyId = proxy is not null ? proxySetting?.Id ?? 0 : 0;

        HttpClientHandler httpClientHandler = new()
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            Proxy = proxy,
            UseProxy = proxy is not null,
        };
        HttpClient httpClient = new(httpClientHandler)
        {
            // Uploads can legitimately run for hours when throttled; rely on the per-request
            // CancellationToken (and server-side timeouts) instead of a fixed total-request timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Always en-US; no language differences
        httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));

        // Set user agent; some sites (Subyshare) don't like the default one
        httpClient.DefaultRequestHeaders.UserAgent.Clear();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:62.0) Gecko/20100101 Firefox/62.0");

        Cookie languageCookie = new("lang", "en", "/", "www.rapidgator.com");
        httpClientHandler.CookieContainer.Add(languageCookie);

        // Mirror the proxy choice into the handler so HTTP transactions log which proxy
        // (if any) was used. Format: "scheme://host:port" — credentials redacted.
        string? proxyDescription = proxySetting is null
            ? null
            : $"{proxySetting.Type.ToString().ToLowerInvariant()}://{proxySetting.Host}:{proxySetting.Port}";
        return new HttpHandler(httpClient, _logger, proxyDescription);
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

    private HttpHandler? HttpHandler { get; set; }

    private string? FileHash { get; set; }

    private string? _currentUploadId;

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
        using var md5 = MD5.Create();
        using FileStream fs = File.OpenRead(filePath);
        byte[] hash = await Hashing.ComputeHashAsync(md5, fs, pauseToken, cancellationToken);

        FileHash = string.Join(string.Empty, hash.Select(s => s.ToString("x2")).ToArray());
    }

    /// <inheritdoc/>
    public override async Task<AccountCheckResult> CheckAccountAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // Build a fresh HttpHandler against the current rotation so the credential check
        // uses the proxy the user has configured *now*, not whatever was active when the
        // client was constructed.
        PrepareHttpHandler();

        try
        {
            UserInfoResponse? response = await HttpLoginAsync(username, password, cancellationToken);
            if (response?.User is null)
            {
                return new AccountCheckResult(false, AccountType.Free, "Login failed. Invalid credentials.");
            }

            AccountType accountType = response.User.IsPremium ? AccountType.Premium : AccountType.Free;
            string message = response.User.IsPremium
                ? $"Premium account (expires {response.User.PremiumEndTime:yyyy-MM-dd})"
                : "Free account";

            return new AccountCheckResult(true, accountType, message, response.User.PremiumEndTime);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, $"Connection error: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override Task UploadAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async Task UploadAsync(string filePath, string username, string password, CancellationToken cancellationToken = default)
    {
        // Always-fresh HttpHandler at the start of an upload attempt so the proxy choice
        // reflects the current ProxyManager state. Retries that re-enter UploadAsync
        // automatically pick the next rotation entry.
        PrepareHttpHandler();

        if (Protocol == Protocol.Http)
        {
            if (UserInfoResponse?.User == null || !string.Equals(UserInfoResponse.User.Email, username, StringComparison.OrdinalIgnoreCase))
            {
                UserInfoResponse = await SharedSessionCache.GetOrCreateAsync(
                    "UserInfoResponse",
                    () => HttpLoginAsync(username, password, cancellationToken));

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
        string link = $"https://{Hostname}/api/v2/user/login?login={username}&password={password}";
        return HttpSendReceiveAsync<UserInfoResponse>(link, "login", cancellationToken);
    }

    private Task<FolderCreateResponse?> HttpCreateFolderAsync(string folderName, UserInfoResponse userInfoResponse, CancellationToken cancellationToken = default)
    {
        string link = $"https://{Hostname}/api/v2/folder/create?name={folderName}&token={userInfoResponse.Token}";
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
        _currentUploadId = fileUploadRequestResponse.Upload.UploadId;

        await HttpHandler.UploadFileAsync(filePath, link, SpeedLimitProvider, cancellationToken);
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
            throw new ArgumentOutOfRangeException(nameof(FolderCreateResponse.Folder), "Folder does not exist.");
        }

        string fileName = Path.GetFileName(filePath);
        string link = $"https://{Hostname}/api/v2/file/upload?folder_id={folderCreateResponse.Folder.Id}&name={fileName}&hash={FileHash}&size={fileInfo.Length}&token={userInfoResponse.Token}";

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

    private async void HttpHandler_UploadFinished(object? sender, ProtocolUploadFinishedEventArgs e)
    {
        DateTime startDateTime = e.DateTimeFinished - e.TimeElapsed;
        string? uploadId = _currentUploadId;
        _currentUploadId = null;

        if (string.IsNullOrEmpty(e.Result))
        {
            UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(e.Success, string.Empty, e.DateTimeFinished));
            return;
        }

        // Try to parse the POST response. If it contains the file info, use it directly.
        if (JsonHelpers.TryDeserializeObject(e.Result, out Response<FileUploadResponse>? fileUploadResponse) && fileUploadResponse is not null)
        {
            if (fileUploadResponse.Status != 200)
            {
                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to upload file: {fileUploadResponse.Status} {fileUploadResponse.Details}");
                UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(false, fileUploadResponse.Details ?? string.Empty, startDateTime));
                return;
            }

            FileHosterFileInfo? fileInfo = ExtractFileInfo(fileUploadResponse.Model);
            if (fileInfo is not null)
            {
                UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(true, fileUploadResponse.Details ?? string.Empty, startDateTime, fileInfo));
                return;
            }

            // No URL in immediate response — poll the upload_info endpoint
            if (!string.IsNullOrEmpty(uploadId) && !string.IsNullOrEmpty(UserInfoResponse?.Token))
            {
                FileHosterFileInfo? polled = await PollUploadInfoAsync(uploadId, UserInfoResponse.Token);
                if (polled is not null)
                {
                    UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(true, "Uploaded", startDateTime, polled));
                    return;
                }

                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Upload finished but URL could not be resolved via polling. upload_id={uploadId}. Raw response: {e.Result}");
            }
            else
            {
                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Upload finished but no upload_id available to poll. Raw response: {e.Result}");
            }

            UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(true, "Uploaded (URL unavailable)", startDateTime));
            return;
        }

        if (JsonHelpers.TryDeserializeObject(e.Result, out Response? response) && !string.IsNullOrEmpty(response.Details))
        {
            _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Failed to upload file: {response.Status} {response.Details}");
            UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(false, response.Details, startDateTime));
            return;
        }

        _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Unrecognized upload response: {e.Result}");
        UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(e.Success, e.Result, startDateTime));
    }

    private static FileHosterFileInfo? ExtractFileInfo(FileUploadResponse? response)
    {
        if (response?.Upload?.File is not { Length: > 0 } files)
        {
            return null;
        }

        FileFile file = files[0];
        if (string.IsNullOrEmpty(file.Url))
        {
            return null;
        }

        FileHosterFileStatus status = file.Mode switch
        {
            0 => FileHosterFileStatus.Free,
            1 => FileHosterFileStatus.PremiumOnly,
            2 => FileHosterFileStatus.Private,
            _ => FileHosterFileStatus.Hotlink,
        };

        return new FileHosterFileInfo
        {
            Id = file.FileId,
            FileStatus = status,
            FileName = file.Name,
            Checksum = file.Hash,
            Url = file.Url,
        };
    }

    private async Task<FileHosterFileInfo?> PollUploadInfoAsync(string uploadId, string token)
    {
        string url = $"https://{Hostname}/api/v2/file/upload_info?sid={uploadId}&token={token}";

        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                FileUploadResponse? info = await HttpSendReceiveAsync<FileUploadResponse>(url, "poll upload info");
                if (info is null)
                {
                    return null;
                }

                FileHosterFileInfo? extracted = ExtractFileInfo(info);
                if (extracted is not null)
                {
                    return extracted;
                }

                // Rapidgator upload states: 0=Uploading, 1=Processing, 2=Done, 3+=Error
                if (info.Upload?.State >= 3)
                {
                    _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Upload polling reports error state={info.Upload.State} label={info.Upload.StateLabel}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Poll attempt {attempt} failed: {ex.Message}");
                return null;
            }
        }

        _logger.Log(this, LogType.Error, $"[{nameof(RapidgatorClient)}] Upload polling timed out after 60 attempts for upload_id={uploadId}");
        return null;
    }

    private void UploadFinishedCallback(string errorMessage) => UploadFinishedCallback(new FileHosterUploadFinishedEventArgs(false, errorMessage, DateTime.Now));

    private void UploadFinishedCallback(FileHosterUploadFinishedEventArgs e) => FireUploadFinished(this, e);
}
