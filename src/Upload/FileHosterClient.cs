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
        { "Ex-Load", "www.ex-load.com" },
        // ExtMatrix DISABLED 2026-06-07 — /api/upload.php gets 413 below ~27 MiB and
        // the web UI's chunked protocol can't be captured (UI also failing). Pipeline
        // DI registration in App.xaml.cs and the EditAccount API-key flag are both
        // commented out alongside this. Do NOT uncomment without re-validating the
        // upload endpoint and walking the re-enable checklist in ExtMatrixPipeline.cs.
        // { "ExtMatrix", "www.extmatrix.com" },
        { "FileBoom", "www.fileboom.me" },
        // Filecloud REMOVED 2026-06-10 — filecloud.io is dead (site down, no live upload
        // endpoint). Never had a pipeline; was metadata-only. Do NOT re-add without
        // confirming the host is back and which protocol family it belongs to.
        // FilesMonster REMOVED 2026-06-12 — filesmonster.com only lets *paid* members
        // upload; free accounts have no upload path, so there's nothing usable to wire up.
        // Never had a pipeline; was metadata-only. Do NOT re-add unless free upload returns.
        // FlashBit DISABLED 2026-06-05 — invalid SSL on fs*.flashbit.cc + IIS chunk-size
        // cap on the storage backend rejects every upload shape we have. Pipeline DI
        // registration in App.xaml.cs and the EditAccount API-key flag are both commented
        // out alongside this. Do NOT uncomment without re-validating both issues are
        // resolved. See FlashBitPipeline.cs class-level remarks for the full chain.
        // { "FlashBit", "flashbit.cc" },
        { "GigaPeta", "gigapeta.com" },
        // Hexload + hexupload.net (alias) — both API and web 301 from .net → .com,
        // so a single Hexload entry covers traffic addressed to either domain.
        { "Hexload", "hexload.com" },
        { "HitFile", "www.hitfile.net" },
        // Hotlink DISABLED 2026-06-23 — hotlink.cc free accounts can't upload (uploading is
        // premium-only: op=upload → "You are not allowed to upload files") and its XFileSharing
        // Pro per-user API key is never rendered on my_account, so the api-key path is impossible.
        // Pipeline DI registration + the EditAccount API-key flag are commented out alongside this.
        // Do NOT re-add without an upload-enabled account AND a logged-in web-upload mode — see
        // HotlinkPipeline.cs class-level remarks for the full diagnosis + re-enable checklist.
        // { "Hotlink", "hotlink.cc" },
        { "Hxfile", "hxfile.co" },
        { "IcerBox", "www.icerbox.com" },
        { "Isracloud", "isra.cloud" },
        // Marketed at katfile.com historically; the live web UI + API both serve from
        // katfile.space now (katfile.cloud also serves the API but 301s for the web
        // routes). See KatFilePipeline for the rationale.
        { "KatFile", "katfile.space" },
        { "Keep2Share", "k2s.cc" },
        { "NitroFlare", "www.nitroflare.com" },
        // Novafile REMOVED 2026-06-27 — novafile.com only allows *premium* user registration;
        // there is no free account to create, so no usable upload path exists. Never had a
        // pipeline; was metadata-only. Do NOT re-add unless free registration + upload returns.
        // Openload REMOVED 2026-06-27 — openload.co was shut down in 2019; the domain is dead
        // with no live upload endpoint. Never had a pipeline; was metadata-only. Do NOT re-add.
        { "Rapidgator", "www.rapidgator.net" },
        // Rapidu REMOVED 2026-06-28 — rapidu.net is down (no live site/upload endpoint). Never had
        // a pipeline; was metadata-only. Do NOT re-add without confirming the host is back and which
        // protocol family it belongs to.
        // RareFile REMOVED 2026-06-28 — metadata-only (never had a pipeline); pruned as an
        // unimplemented hoster. rarefile.net is an XFileSharing host, so a re-add would be an
        // XFileSharingApiPipeline shim — but only after confirming the host is live + uploadable.
        // ShareOnline REMOVED 2026-06-28 — share-online.biz shut down in 2019; the domain is dead
        // with no live upload endpoint. Never had a pipeline; was metadata-only. Do NOT re-add.
        // TakeFile DISABLED 2026-06-28 — takefile.link's whole domain is behind a Cloudflare
        // *managed* challenge that fingerprints the TLS stack, so the C# my_account scrape (and
        // every other request) gets the "Just a moment…" interstitial. cf_clearance forwarding was
        // implemented + tested but a managed challenge rejects a .NET client even with a valid
        // clearance + matching UA + IP. Pipeline DI registration + the EditAccount ApiKeyHosters
        // entry are commented out alongside this. Do NOT re-add without confirming TakeFile dropped
        // the managed challenge. See TakeFilePipeline.cs class-level remarks. (Live host was the apex
        // takefile.link; www. redirected.)
        // { "TakeFile", "takefile.link" },
        { "TezFiles", "tezfiles.com" },
        // UbiqFile REMOVED 2026-06-28 — ubiqfile.com's main domain is behind a Cloudflare *managed*
        // challenge (cType:'managed', same TLS-fingerprint wall TakeFile hit), so the C# my_account /
        // upload-form scrape can't pass it even with cf_clearance. Its upload servers (uNN.ubiqfile.com)
        // are direct nginx, but the rotating upload server + sess_id must be scraped from the
        // Cloudflare-protected main domain first, so the open upload node is unreachable. The free tier
        // is also near-useless (1 MB per file AND 1 MB total storage). Never had a pipeline; metadata-only.
        // Do NOT re-add without confirming the managed challenge is gone. See takefile-disabled rationale.
        { "Uploaded", "www.uploaded.net" },
        { "UploadGIG", "www.uploadgig.com" },
        { "UniBytes", "www.unibytes.com" },
        { "Upstore", "upstore.net" },
        { "WuShare", "www.wushare.com" },
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Hoster display names sorted case-insensitively for UI lists (Add Account dialog,
    /// upload wizard grid). The underlying <see cref="FileHosters"/> dictionary is
    /// authored in arbitrary order and <see cref="FrozenDictionary{TKey,TValue}.Keys"/>
    /// does not promise a stable enumeration order — sort once here so every consumer
    /// gets the same alphabetical view.
    /// </summary>
    public static IReadOnlyList<string> NamesAlphabetical { get; } =
        [.. FileHosters.Keys.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];

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
    public static FileHosterClient? FindByHost(string name, Protocol protocol, IAppLogger _) => FileHosters.ContainsKey(name) ? new FileHosterClient(name, protocol) : null;
}
