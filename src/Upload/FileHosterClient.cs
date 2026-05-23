// <copyright file="FileHosterClient.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Frozen;
using CSUploader.Lib;
using CSUploader.Lib.Net;

namespace CSUploader.Upload;

/// <summary>
/// Metadata for a file hoster: its display name, protocol, and the master list of all
/// known hosters. Upload behavior lives in <see cref="Pipeline.IFileHosterPipeline"/>
/// implementations resolved via <see cref="Pipeline.IFileHosterRegistry"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FileHosterClient"/> class.
/// </remarks>
/// <param name="name">The display name of the file hoster.</param>
/// <param name="protocol">The protocol used for uploading.</param>
public sealed class FileHosterClient(string name, Protocol protocol)
{
    /// <summary>
    /// Master metadata table — display name → primary host. <see cref="FrozenDictionary{TKey, TValue}"/>
    /// is built once and read many times: hashing is pre-computed at build time and
    /// `ContainsKey` / `Keys` are 5–10× faster than the equivalent `Dictionary`. Read-only
    /// safety still holds since `FrozenDictionary` exposes no mutating API.
    /// </summary>
    public static FrozenDictionary<string, string> FileHosters { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "Alfafile", "www.alfafile.net" },
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
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Gets the name of the file hoster.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the protocol used for uploading by the file hoster.
    /// </summary>
    public Protocol Protocol { get; } = protocol;

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
