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
        //{ Alfafile.Name_, "www.alfafile.net" },
        //{ AndroidFileHost.Name_, "www.androidfilehost.com" },
        //{ BRupload.Name_, "www.brupload.net" },
        //{ Datafile.Name_, "www.datafile.com" },
        //{ ExLoad.Name_, "www.ex-load.com" },
        //{ ExtMatrix.Name_, "www.extmatrix.com" },
        //{ FileBoom.Name_, "www.fileboom.me" },
        //{ Filecloud.Name_, "filecloud.io" },
        //{ FilesMonster.Name_, "www.filesmonster.com" },
        //{ FlashBit.Name_, "flashbit.cc" },
        //{ GigaPeta.Name_, "gigapeta.com" },
        //{ HitFile.Name_, "www.hitfile.net" },
        //{ IcerBox.Name_, "www.icerbox.com" },
        //{ IsraCloud.Name_, "www.isra.cloud" },
        //{ KatFile.Name_, "www.katfile.com" },
        //{ Keep2Share.Name_, "k2s.cc" },
        //{ NitroFlare.Name_, "www.nitroflare.com" },
        //{ Novafile.Name_, "novafile.com" },
        //{ Openload.Name_, "openload.co" },
        { "Rapidgator", "www.rapidgator.net" },
        //{ Rapidu.Name_, "www.rapidu.net" },
        //{ RareFile.Name_, "www.rarefile.net" },
        //{ ShareOnline.Name_, "www.share-online.biz" },
        //{ TakeFile.Name_, "www.takefile.link" },
        //{ TezFiles.Name_, "tezfiles.com" },
        //{ UbiqFile.Name_, "www.ubiqfile.com" },
        //{ Uploaded.Name_, "www.uploaded.net" },
        //{ UploadGIG.Name_, "www.uploadgig.com" },
        //{ UniBytes.Name_, "www.unibytes.com" },
        //{ Upstore.Name_, "upstore.net" },
        //{ Uptobox.Name_, "www.uptobox.com" },
        //{ WuShare.Name_, "www.wushare.com" },
    });

    private static Dictionary<string, Func<Protocol, FileHosterClient>> FileHosterFactory { get; } = new Dictionary<string, Func<Protocol, FileHosterClient>>
    {
        //{ Alfafile.Name_, () => new Alfafile() },
        //{ AndroidFileHost.Name_, () => new AndroidFileHost() },
        //{ BRupload.Name_, () => new BRupload() },
        //{ Datafile.Name_, () => new Datafile() },
        //{ ExLoad.Name_, () => new ExLoad() },
        //{ ExtMatrix.Name_, () => new ExtMatrix() },
        //{ FileBoom.Name_, () => new FileBoom() },
        //{ Filecloud.Name_, () => new Filecloud() },
        //{ FilesMonster.Name_, () => new FilesMonster() },
        //{ FlashBit.Name_, () => new FlashBit() },
        //{ GigaPeta.Name_, () => new GigaPeta() },
        //{ HitFile.Name_, () => new HitFile() },
        //{ IcerBox.Name_, () => new IcerBox() },
        //{ IsraCloud.Name_, () => new IsraCloud() },
        //{ KatFile.Name_, () => new KatFile() },
        //{ Keep2Share.Name_, () => new Keep2Share() },
        //{ NitroFlare.Name_, () => new NitroFlare() },
        //{ Novafile.Name_, () => new Novafile() },
        //{ Openload.Name_, () => new Openload() },
        { "Rapidgator", (Protocol protocol) => new RapidgatorClient(protocol) },
        //{ Rapidu.Name_, () => new Rapidu() },
        //{ RareFile.Name_, () => new RareFile() },
        //{ ShareOnline.Name_, () => new ShareOnline() },
        //{ TakeFile.Name_, () => new TakeFile() },
        //{ TezFiles.Name_, () => new TezFiles() },
        //{ UbiqFile.Name_, () => new UbiqFile() },
        //{ Uploaded.Name_, () => new Uploaded() },
        //{ UploadGIG.Name_, () => new UploadGIG() },
        //{ UniBytes.Name_, () => new UniBytes() },
        //{ Upstore.Name_, () => new Upstore() },
        //{ Uptobox.Name_, () => new Uptobox() },
        //{ WuShare.Name_, () => new WuShare() }
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
    public event EventHandler<FileHosterUploadProgressEventArgs>? UploadProgress;

    /// <summary>
    /// Occurs when uploading has finished.
    /// </summary>
    public event EventHandler<FileHosterUploadFinishedEventArgs>? UploadFinished;

    /// <summary>
    /// Occurs when hashing is in progress.
    /// </summary>
    public event EventHandler<FileHosterHashingProgressEventArgs>? HashingProgress;

    /// <summary>
    /// Occurs when hashing has finished.
    /// </summary>
    public event EventHandler<FileHosterHashingFinishedEventArgs>? HashingFinished;

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
    /// <value>
    /// The hashing.
    /// </value>
    protected Hashing Hashing { get; }

    /// <summary>
    /// Returns an instance of a file hoster client for the specified name and protocol.
    /// </summary>
    /// <param name="name">The name of the file hoster.</param>
    /// <param name="protocol">The protocol the file hoster should use to upload.</param>
    /// <returns>An instance of a file hoster client if found; otherwise, null.</returns>
    public static FileHosterClient? FindByHost(string name, Protocol protocol)
    {
        return FileHosterFactory
                .Where(fh => string.Equals(fh.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(fh => fh.Value(protocol))
                .FirstOrDefault();
    }

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
    public virtual Task HashAsync(string filePath, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fires the upload progress event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="FileHosterUploadProgressEventArgs"/> instance containing the event data.</param>
    protected virtual void FireUploadProgress(object sender, FileHosterUploadProgressEventArgs e)
    {
        UploadProgress?.Invoke(sender, e);
    }

    /// <summary>
    /// Fires the upload finished event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="FileHosterUploadFinishedEventArgs"/> instance containing the event data.</param>
    protected virtual void FireUploadFinished(object sender, FileHosterUploadFinishedEventArgs e)
    {
        UploadFinished?.Invoke(sender, e);
    }

    /// <summary>
    /// Fires the hashing progress event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="FileHosterHashingProgressEventArgs"/> instance containing the event data.</param>
    protected virtual void FireHashingProgress(object sender, FileHosterHashingProgressEventArgs e)
    {
        HashingProgress?.Invoke(sender, e);
    }

    /// <summary>
    /// Fires the hashing finished event.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="FileHosterHashingFinishedEventArgs"/> instance containing the event data.</param>
    protected virtual void FireHashingFinished(object sender, FileHosterHashingFinishedEventArgs e)
    {
        HashingFinished?.Invoke(sender, e);
    }

    private void Hashing_HashingProgress(object? sender, HashingProgressEventArgs e)
    {
        FireHashingProgress(this, new FileHosterHashingProgressEventArgs(e));
    }

    private void Hashing_HashingFinished(object? sender, HashingFinishedEventArgs e)
    {
        FireHashingFinished(this, new FileHosterHashingFinishedEventArgs(e));
    }
}
