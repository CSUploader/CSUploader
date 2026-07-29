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
        // 1fichier.com anonymous upload — GET / for a rotating node + per-upload session id
        // (upNN.1fichier.com/upload.cgi?id=XID), one multipart POST of file[], then the result page the
        // 302 names (end.pl?xid=XID) carries the link. Guest cap 5 GB. See OneFichierPipeline.cs.
        { "1Fichier", "1fichier.com" },
        { "Alfafile", "www.alfafile.net" },
        { "BRupload", "www.brupload.net" },
        // buzzheavier.com — anonymous OR account via the developer API: a single raw PUT to
        // w.buzzheavier.com/<name> → {"data":{"id":…}}; link buzzheavier.com/<id>. Account auth is a
        // Bearer of the account id (from /api/account, captured via a WebView sign-in). See BuzzheavierPipeline.cs.
        { "Buzzheavier", "buzzheavier.com" },
        // catbox.moe anonymous upload — a single multipart POST to /user/api.php (reqtype=fileupload),
        // response is the plain files.catbox.moe URL. No account. See CatboxPipeline.cs.
        { "Catbox", "catbox.moe" },
        // DropGalaxy DISABLED 2026-07-26 (the day it was added) — anonymous uploads are capped at
        // 0.00001 MB (~10 bytes; the host answers "File size limit is 0.00001 Mbytes"), and account
        // registration is closed, so the API-key path can't be reached either. The pipeline is a
        // correct XFS shim and is retained; DI registration + the ApiKeyHosters entry are commented
        // out alongside this. Do NOT re-add without a usable cap or open registration — see
        // DropGalaxyPipeline.cs class-level remarks for the full diagnosis + re-enable checklist.
        // { "DropGalaxy", "dropgalaxy.com" },
        { "Ex-Load", "www.ex-load.com" },
        // ExtMatrix DISABLED 2026-06-07 — /api/upload.php gets 413 below ~27 MiB and
        // the web UI's chunked protocol can't be captured (UI also failing). Pipeline
        // DI registration in App.xaml.cs and the EditAccount API-key flag are both
        // commented out alongside this. Do NOT uncomment without re-validating the
        // upload endpoint and walking the re-enable checklist in ExtMatrixPipeline.cs.
        // { "ExtMatrix", "www.extmatrix.com" },
        { "FileBoom", "www.fileboom.me" },
        // filegarden.com account upload — login (POST api.filegarden.com/token → auth cookie + userId) →
        // POST /users/<userId>/pipe (raw body + X-Data header) → {"id","path"}; link filegarden.com/<userId>/<path>.
        { "FileGarden", "filegarden.com" },
        // filehoster.io is an XFileSharing host on the "xfspro" chunked-upload plugin; account-only
        // (login → start_upload → put_chunk.cgi → import_file). See FilehosterIoPipeline.cs.
        { "Filehoster.io", "filehoster.io" },
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
        // gofile.io anonymous upload — guest account (POST /accounts) → createfolder → multipart
        // upload.gofile.io/uploadfile; share link is the response's downloadPage. See GofilePipeline.cs.
        { "Gofile", "gofile.io" },
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
        // mega.nz account upload — MEGA's end-to-end-encrypted protocol (g.api.mega.co.nz), the same
        // WebSocket chunk upload transfer.it uses but into a real account's Cloud Drive. See MegaPipeline.cs
        // + the Mega/ helpers. (Transfer.it is the anonymous/ephemeral sibling.)
        { "MEGA", "mega.nz" },
        // mediafire.com account upload — web login (user cookie) → session token → SHA-256 hash-dedup
        // check → instant link OR raw simple.php byte upload + poll. See MediaFirePipeline.cs.
        { "MediaFire", "www.mediafire.com" },
        { "NitroFlare", "www.nitroflare.com" },
        // pixeldrain.com account upload — login (pd_auth_key cookie) → PUT /api/file/<name> raw body →
        // {"id":...}; share link pixeldrain.com/u/<id>. Anonymous upload was removed. See PixeldrainPipeline.cs.
        { "Pixeldrain", "pixeldrain.com" },
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
        // storage.to is a Laravel front end that hands bytes to Cloudflare R2 via a presigned PUT
        // (anonymous, no login). See StorageToPipeline.cs.
        // Send.now — classic XFileSharing, anonymous web-form upload. Formerly send.cm (tusfiles /
        // sendit lineage); send.cm 301s here, so this single entry covers both. See SendNowPipeline.cs.
        { "Send.now", "send.now" },
        { "Storage.to", "storage.to" },
        { "TezFiles", "tezfiles.com" },
        // transfer.it is a frontend over MEGA — uploads use MEGA's end-to-end-encrypted protocol
        // (anonymous ephemeral session). See TransferItPipeline.cs + the Mega/ helpers.
        { "Transfer.it", "transfer.it" },
        // UbiqFile REMOVED 2026-06-28 — ubiqfile.com's main domain is behind a Cloudflare *managed*
        // challenge (cType:'managed', same TLS-fingerprint wall TakeFile hit), so the C# my_account /
        // upload-form scrape can't pass it even with cf_clearance. Its upload servers (uNN.ubiqfile.com)
        // are direct nginx, but the rotating upload server + sess_id must be scraped from the
        // Cloudflare-protected main domain first, so the open upload node is unreachable. The free tier
        // is also near-useless (1 MB per file AND 1 MB total storage). Never had a pipeline; metadata-only.
        // Do NOT re-add without confirming the managed challenge is gone. See takefile-disabled rationale.
        // Uploaded REMOVED 2026-06-28 — uploaded.net (uploaded.to) shut down in 2019; the domain is
        // dead with no live upload endpoint. Never had a pipeline; was metadata-only. Do NOT re-add.
        // UploadGIG DISABLED 2026-06-28 — couldn't get a working upload capture to reverse-engineer
        // the protocol (uploads to uploadgig.com aren't going through), so there's nothing to wire up
        // yet. Metadata-only (never had a pipeline). Commented out (not removed) so it's not a
        // selectable-but-broken hoster; the icon + PNG are retained for an easy re-enable once a
        // capture is available. Un-comment this line and build the pipeline when uploads work again.
        // { "UploadGIG", "www.uploadgig.com" },
        // UniBytes REMOVED 2026-06-28 — unibytes.com is down (no live site/upload endpoint). Never
        // had a pipeline; was metadata-only. Do NOT re-add without confirming the host is back and
        // which protocol family it belongs to.
        // ufile.io anonymous chunked upload — GET / (csrf+session) → select_storage → create_session →
        // 99 MB multipart chunks → finalise → ufile.io/<slug>. See UfileIoPipeline.cs.
        { "Ufile", "ufile.io" },
        // Uploady — classic XFileSharing on the web-form (no-API) path: account-only, because its
        // anonymous upload is broken server-side. Upload nodes are on gamezizo.com. See UploadyPipeline.cs.
        { "Uploady", "uploady.io" },
        { "Upstore", "upstore.net" },
        // wormhole.app is a WebTorrent + RFC 8188 E2E + Backblaze B2 uploader (anonymous, no login); the
        // link carries the decryption key in its #fragment. See WormholePipeline.cs + the Wormhole/ helpers.
        { "Wormhole", "wormhole.app" },
        // WuShare REMOVED 2026-06-28 — wushare.com is dead (refuses connections; no working upload).
        // Never had a pipeline; was metadata-only. Do NOT re-add without confirming the host is back.
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
    /// Hosters with genuinely unlimited storage that expose NO usage figure — so the Accounts grid's
    /// "Available" cell should read "Unlimited" (the account is fine; there's just no number to compute
    /// remaining space from), distinct from a hoster whose usage we simply couldn't retrieve (which
    /// shows "-"). catbox.moe: files never expire, no cap, and no used/quota metric on the account page.
    /// Buzzheavier likewise has no cap and exposes no usage figure.
    /// A hoster that DOES report a used figure but no cap (Upstore, GigaPeta) already shows "Unlimited"
    /// via the used-known-no-quota path and needn't be listed here.
    /// </summary>
    private static readonly FrozenSet<string> _unlimitedStorageHosters =
        new HashSet<string>(StringComparer.Ordinal) { "Catbox", "Buzzheavier" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when the hoster has unlimited storage but reports no usage figure — see
    /// <see cref="_unlimitedStorageHosters"/>.</summary>
    public static bool HasUnlimitedStorage(string? hosterName)
        => hosterName is not null && _unlimitedStorageHosters.Contains(hosterName);

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
