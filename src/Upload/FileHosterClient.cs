// <copyright file="FileHosterClient.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;

namespace CSUploader.Upload;

/// <summary>
/// The base class for file hoster clients.
/// </summary>
public abstract class FileHosterClient
{
    public static ReadOnlyDictionary<string, string> FileHosters { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
    {
        { "Alfafile", "www.alfafile.net" },
        { "AndroidFileHost", "www.androidfilehost.com" },
        { "BRupload", "www.brupload.net" },
        { "ExLoad", "www.ex-load.com" },
        { "ExtMatrix", "www.extmatrix.com" },
        { "FileBoom", "www.fileboom.me" },
        { "Filecloud", "filecloud.io" },
        { "FilesMonster", "www.filesmonster.com" },
        { "FlashBit", "flashbit.cc" },
        { "GigaPeta", "gigapeta.com" },
        { "HitFile", "www.hitfile.net" },
        { "IcerBox", "www.icerbox.com" },
        { "IsraCloud", "www.isra.cloud" },
        { "KatFile", "www.katfile.com" },
        { "Keep2Share", "k2s.cc" },
        { "NitroFlare", "www.nitroflare.com" },
        { "Novafile", "novafile.com" },
        { "Openload", "openload.co" },
        { "Rapidgator", "www.rapidgator.net" },
        { "Rapidu", "www.rapidu.net" },
        { "RareFile", "www.rarefile.net" },
        { "ShareOnline", "www.share-online.biz" },
        { "TakeFile", "www.takefile.link" },
        { "TezFiles", "tezfiles.com" },
        { "UbiqFile", "www.ubiqfile.com" },
        { "Uploaded", "www.uploaded.net" },
        { "UploadGIG", "www.uploadgig.com" },
        { "UniBytes", "www.unibytes.com" },
        { "Upstore", "upstore.net" },
        { "WuShare", "www.wushare.com" },
    });

    private static Dictionary<string, Func<Protocol, IAppLogger, FileHosterClient>> FileHosterFactory { get; } = new Dictionary<string, Func<Protocol, IAppLogger, FileHosterClient>>
    {
        { "Rapidgator", (Protocol protocol, IAppLogger logger) => new RapidgatorClient(protocol, logger) },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FileHosterClient"/> class.
    /// </summary>
    protected FileHosterClient()
    {
        Hashing = new Hashing();
        Hashing.HashingProgress += Hashing_HashingProgress;
        Hashing.HashingFinished += Hashing_HashingFinished;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileHosterClient"/> class.
    /// </summary>
    /// <param name="protocol">The protocol.</param>
    protected FileHosterClient(Protocol protocol)
        : this()
    {
        Protocol = protocol;
    }

    /// <summary>
    /// Occurs when uploading is in progress.
    /// </summary>
    public event EventHandler<OperationProgressEventArgs>? UploadProgress;

    /// <summary>
    /// Occurs when uploading has finished.
    /// </summary>
    public event EventHandler<FileHosterUploadFinishedEventArgs>? UploadFinished;

    /// <summary>
    /// Occurs when hashing is in progress.
    /// </summary>
    public event EventHandler<OperationProgressEventArgs>? HashingProgress;

    /// <summary>
    /// Occurs when hashing has finished.
    /// </summary>
    public event EventHandler<HashingFinishedEventArgs>? HashingFinished;

    /// <summary>
    /// Gets the name of the file hoster.
    /// </summary>
    /// <value>
    /// The name of the file hoster.
    /// </value>
    public abstract string Name { get; }

    /// <summary>
    /// Gets or sets the protocol used for uploading by the file hoster.
    /// </summary>
    /// <value>
    /// The protocol used or uploading by the file hoster.
    /// </value>
    public Protocol Protocol { get; protected set; }

    /// <summary>
    /// Gets or sets a value indicating whether hashing is required before uploading a file.
    /// </summary>
    public virtual bool RequiresHashingBeforeUpload { get; protected set; }

    /// <summary>
    /// Gets or sets a value indicating whether hashing is required after uploading a file has finished.
    /// </summary>
    public virtual bool RequiresHashingAfterUpload { get; protected set; }

    /// <summary>
    /// Gets the hashing.
    /// </summary>
    protected Hashing Hashing { get; }

    /// <summary>
    /// Gets or sets a shared session cache scoped per-package per-hoster.
    /// All client instances for the same hoster within a package share this instance.
    /// </summary>
    internal SharedSession SharedSessionCache { get; set; } = new();

    /// <summary>
    /// Provider returning the current effective upload speed limit in bytes/second.
    /// Returning null or a non-positive value means no throttling.
    /// </summary>
    public Func<long?>? SpeedLimitProvider { get; set; }

    /// <summary>
    /// Id of the proxy this client is currently routed through (0 = direct connection).
    /// Test-observable: lets the proxy-rotation tests assert which proxy a freshly-built
    /// client picked.
    /// </summary>
    public int ActiveProxyId { get; set; }

    /// <summary>
    /// Rebuilds the underlying HTTP transport so the next request picks a fresh proxy
    /// from <see cref="Lib.Net.ProxyManager.NextProxy"/>. Called when a failed file is
    /// retried so a bad proxy doesn't poison every retry. Default implementation is a
    /// no-op for hosters that don't use a long-lived HttpClient.
    /// </summary>
    public virtual void RefreshConnection()
    {
    }

    /// <summary>
    /// Returns an instance of a file hoster client for the specified name and protocol.
    /// </summary>
    /// <param name="name">The name of the file hoster.</param>
    /// <param name="protocol">The protocol the file hoster should use to upload.</param>
    /// <param name="logger">The application logger.</param>
    /// <returns>An instance of a file hoster client if found; otherwise, null.</returns>
    public static FileHosterClient? FindByHost(string name, Protocol protocol, IAppLogger logger)
    {
        return FileHosterFactory
                .Where(fh => string.Equals(fh.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(fh => fh.Value(protocol, logger))
                .FirstOrDefault();
    }

    /// <summary>
    /// Checks the account credentials and returns the account type.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the account check.</returns>
    public virtual Task<AccountCheckResult> CheckAccountAsync(string username, string password, CancellationToken cancellationToken = default) => Task.FromResult(new AccountCheckResult(false, AccountType.Free, "Account checking not implemented for this hoster."));

    /// <summary>
    /// Upload a file asynchronously.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    public abstract Task UploadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload a file asynchronously.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    public abstract Task UploadAsync(string filePath, string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hash a file asynchronously.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="pauseToken">The pause token.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous operation.</returns>
    public virtual Task HashAsync(string filePath, PauseToken pauseToken = default, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Fires the upload progress event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="OperationProgressEventArgs"/> instance containing the event data.</param>
    protected virtual void FireUploadProgress(object sender, OperationProgressEventArgs e) => UploadProgress?.Invoke(sender, e);

    /// <summary>
    /// Fires the upload finished event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="FileHosterUploadFinishedEventArgs"/> instance containing the event data.</param>
    protected virtual void FireUploadFinished(object sender, FileHosterUploadFinishedEventArgs e) => UploadFinished?.Invoke(sender, e);

    /// <summary>
    /// Fires the hashing progress event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="OperationProgressEventArgs"/> instance containing the event data.</param>
    protected virtual void FireHashingProgress(object sender, OperationProgressEventArgs e) => HashingProgress?.Invoke(sender, e);

    /// <summary>
    /// Fires the hashing finished event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="HashingFinishedEventArgs"/> instance containing the event data.</param>
    protected virtual void FireHashingFinished(object sender, HashingFinishedEventArgs e) => HashingFinished?.Invoke(sender, e);

    private void Hashing_HashingProgress(object? sender, OperationProgressEventArgs e) => FireHashingProgress(this, e);

    private void Hashing_HashingFinished(object? sender, HashingFinishedEventArgs e) => FireHashingFinished(this, e);
}
