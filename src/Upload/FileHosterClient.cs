// <copyright file="FileHosterClient.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CSUploader.Lib;
using CSUploader.Lib.Net;

namespace CSUploader.Upload;

/// <summary>
/// Metadata for a file hoster: its display name, protocol, and the master list of all
/// known hosters. Upload behavior lives in <see cref="Pipeline.IFileHosterPipeline"/>
/// implementations resolved via <see cref="Pipeline.IFileHosterRegistry"/>.
/// </summary>
public sealed class FileHosterClient
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

    /// <summary>
    /// Initializes a new instance of the <see cref="FileHosterClient"/> class.
    /// </summary>
    /// <param name="name">The display name of the file hoster.</param>
    /// <param name="protocol">The protocol used for uploading.</param>
    public FileHosterClient(string name, Protocol protocol)
    {
        Name = name;
        Protocol = protocol;
    }

    /// <summary>
    /// Gets the name of the file hoster.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the protocol used for uploading by the file hoster.
    /// </summary>
    public Protocol Protocol { get; }

    /// <summary>
    /// Checks the account credentials and returns the account type.
    /// Returns a "not implemented" result for hosters that have not yet migrated their
    /// account-check logic to an <see cref="Pipeline.IFileHosterPipeline"/> implementation.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the account check.</returns>
    public static Task<AccountCheckResult> CheckAccountAsync(string username, string password, CancellationToken cancellationToken = default)
        => Task.FromResult(new AccountCheckResult(false, AccountType.Free, "Account checking not implemented for this hoster."));

    /// <summary>
    /// Returns metadata for the named hoster, or null if it isn't in <see cref="FileHosters"/>.
    /// Pre-Phase-4 this returned hoster-client instances; the abstract base is gone now.
    /// </summary>
    /// <param name="name">The name of the file hoster.</param>
    /// <param name="protocol">The protocol the file hoster should use to upload.</param>
    /// <param name="_">The application logger (unused; retained for call-site compatibility).</param>
    /// <returns>A metadata instance if the hoster is known; otherwise, null.</returns>
    public static FileHosterClient? FindByHost(string name, Protocol protocol, IAppLogger _)
    {
        return FileHosters.ContainsKey(name) ? new FileHosterClient(name, protocol) : null;
    }
}
