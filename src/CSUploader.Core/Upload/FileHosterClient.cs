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
        // Clicknupload — stock XFileSharing, ACCOUNT-ONLY (guests are refused outright). Web-form path:
        // WebView sign-in for xfss → GET ?op=my_account.html → scrape the form action + sess_id →
        // classic multipart. Domain rotates (.click/.org/.co/.vip). See ClicknuploadPipeline.cs.
        { "Clicknupload", "clicknupload.click" },
        // DropGalaxy DISABLED 2026-07-26 (the day it was added) — anonymous uploads are capped at
        // 0.00001 MB (~10 bytes; the host answers "File size limit is 0.00001 Mbytes"), and account
        // registration is closed, so the API-key path can't be reached either. The pipeline is a
        // correct XFS shim and is retained; DI registration + the ApiKeyHosters entry are commented
        // out alongside this. Do NOT re-add without a usable cap or open registration — see
        // DropGalaxyPipeline.cs class-level remarks for the full diagnosis + re-enable checklist.
        // { "DropGalaxy", "dropgalaxy.com" },
        // ddownload.com (ex ddl.to) — XFileSharing Pro, API-key path. Its /api/* is served ONLY from
        // api-v2.ddownload.com; links and my_account stay here. Free accounts can upload (verified
        // 2026-08-01). See DDownloadPipeline.cs.
        { "DDownload", "ddownload.com" },
        { "Ex-Load", "www.ex-load.com" },
        // ExtMatrix DISABLED 2026-06-07 — /api/upload.php gets 413 below ~27 MiB and
        // the web UI's chunked protocol can't be captured (UI also failing). Pipeline
        // DI registration in App.xaml.cs and the EditAccount API-key flag are both
        // commented out alongside this. Do NOT uncomment without re-validating the
        // upload endpoint and walking the re-enable checklist in ExtMatrixPipeline.cs.
        // { "ExtMatrix", "www.extmatrix.com" },
        // filestank.com — YetiShare, NOT XFileSharing. Account-only, on the session-cookie path:
        // WebView sign-in (filehosting cookie) → per-upload uploader.js scrape for the node URL +
        // _sessionid + cTracker → blueimp multipart files[] to strN.filestank.com. See
        // FilestankPipeline.cs, including why its published /api/v2 isn't the shipped path.
        { "Filestank", "www.filestank.com" },
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
        // usersdrive.com — classic XFS, ANONYMOUS upload live (homepage form → dNNN.userdrive.org
        // upload.cgi?utype=anon, empty sess_id). 5250 MB guest cap. See UsersDrivePipeline.cs.
        { "UsersDrive", "usersdrive.com" },
        // turbobit.net — HitFile's sibling (same operator/platform): WebView sign-in yields a durable
        // appId, then POST app.turbobit.net/api/upload/urls → multipart Filedata + apptype=fd1 +
        // user_id=appId → {"id"}; link turbobit.net/<id>.html. Account-only. See TurbobitPipeline.cs.
        { "Turbobit", "turbobit.net" },
        { "Upstore", "upstore.net" },
        // vikingfile.com anonymous upload over its own documented API: POST /api/get-upload-url (size)
        // → presigned Cloudflare-R2 part PUTs (keep each ETag) → POST /api/complete-upload with an
        // EMPTY user → {hash,url}; link vikingfile.com/f/<hash>. See VikingFilePipeline.cs.
        { "VikingFile", "vikingfile.com" },
        // dailyuploads.net — ANONYMOUS xfspro, same base as FILEAXA. Its finalise returns only a
        // file_code (no links object), so the link is dailyuploads.net/<code>. See DailyUploadsPipeline.cs.
        { "DailyUploads", "dailyuploads.net" },
        // fileaxa.com — ANONYMOUS upload on the XFileSharing "xfspro" chunked plugin (filehoster.io's
        // family): GET /server → node, PUT put_chunk.cgi + X-Upload-SID, then a MULTIPART api.cgi
        // op=import_file whose sess_id is simply left empty. It also exposes the XFS REST API, but its
        // own client never uses it. See FileaxaPipeline.cs.
        { "FILEAXA", "fileaxa.com" },
        // uploadrar.com — classic XFileSharing on the REST API path (/api/account/info, /api/upload/server).
        // ACCOUNT-only: ?op=api_get_limits reports MaxUploadFilesize 0.00001 for a signed-out caller.
        // ⚠ It BLOCKS mp4/mpg/wmv/mkv/m4v/avi/mp3 and only enforces that at the finalise step, so the
        // pipeline pre-checks the extension locally. See UploadrarPipeline.cs.
        { "Uploadrar", "uploadrar.com" },
        // filedot.to — XFileSharing, ACCOUNT-only ("uploads are not enabled for your account type" to
        // an anonymous post). Ships on SIGN-IN: the REST API answers, but no page in the signed-in UI
        // hands out a key. Node comes from GET /server (the file form has no action — the only one on
        // the page is the URL-uploader's). 5 GB/file, 10 TB storage, BLOCKS exe/jpg/jpeg/gif/png.
        // See FiledotPipeline.cs.
        // easybytez.org — XFileSharing "xfspro" chunked WITH a session (op=start_upload → put_chunk.cgi
        // → form-urlencoded import_file), i.e. filehoster.io's twin; they share XfsProSessionPipeline.
        // ACCOUNT-only: its upload page renders a utype=anon guest form, but the node answers
        // "uploads are not enabled for your account type" — the form is decoration. Registered tier is
        // 200 MB/file + 10 GB storage (guests 10 MB, premium 7000 MB). Plain username/password login,
        // no captcha. See EasybytezPipeline.cs.
        // elitefile.net — stock XFileSharing on the web-form (sign-in) path; every route is the family
        // default and its form action already carries upload_type=file&utype=reg. ACCOUNT-only: no REST
        // API at all (/api/upload/server 404s). ⚠ Uploads answer {"domain":"https://elfile.net",…} and
        // the link lives THERE, not on elitefile.net — the base honours that field. No per-file cap
        // (max_upload_filesize 0), 488 GB storage. See EliteFilePipeline.cs.
        { "EliteFile", "elitefile.net" },
        // temp.sh — ANONYMOUS, no accounts at all. One multipart POST to /upload (field "file"); the
        // response BODY is the plain share URL, exactly as its homepage documents with curl. 4 GB.
        // ⚠ Files expire after 3 days — a transfer service, not storage. See TempShPipeline.cs.
        { "Temp.sh", "temp.sh" },
        // litterbox.catbox.moe — catbox.moe's temporary sibling: same /resources/internals/api.php
        // shape (reqtype=fileupload + fileToUpload) plus a `time` field. ANONYMOUS, 1 GB — five times
        // catbox's cap, in exchange for deletion. ⚠ 72 h is the longest retention offered. Note the link
        // host is litter.catbox.moe, which the server names itself. See LitterboxPipeline.cs.
        { "Litterbox", "litterbox.catbox.moe" },
        // tmpfiles.org — ANONYMOUS, no accounts, and it documents its own API (/api). One multipart POST
        // to /api/v1/upload (field "file") → {"status":"success","data":{"url":…}}. 100 MB.
        // ⚠ Retention defaults to ONE HOUR; we always send expire=172800 (48 h, its documented maximum)
        // — measured: 47h59m with the field, 59 minutes without. See TmpFilesPipeline.cs.
        { "TmpFiles", "tmpfiles.org" },
        // qu.ax — ANONYMOUS, no accounts. One multipart POST to /upload.php (files[] + expiry) →
        // {"success":true,"files":[{"url":…}]}. 256 MB. Files can be PERMANENT: expiry=-1 is the host's
        // own option and is what we send — omitting the field takes its 30-day default.
        // ⚠ ALLOWLIST (.rar/.zip/.7z/.tar/.gz/images/video/pdf/txt): .r00, .001, .sfv and .nfo are
        // REFUSED, so a classic multi-part set only half-uploads here while .partN.rar is fine. The
        // pipeline checks locally because the host refuses after the bytes arrive.
        // Also de-duplicates by content hash — identical bytes return the same link.
        { "Qu.ax", "qu.ax" },
        // upload.ee — ANONYMOUS or ACCOUNT. Uber-Uploader (Perl CGI), a family nothing else here uses:
        // GET /ubr_link_upload.php?rnd_id=<ms> hands back a SERVER-MINTED id inside a line of
        // JavaScript, then a multipart POST to /cgi-bin/ubr_upload.pl?X-Progress-ID=&upload_id= with
        // ONLY upfile_0, then /?page=finished&upload_id= renders the link plus a ?killcode= delete URL.
        // ⚠ Inventing the id does not work — the server writes a .link file when it issues one, and an
        // id it never issued dies inside their Perl. ⚠ It answers a browser with 302 and this client
        // with 200 + parent.location.href; both are handled. 100 MB anonymous, kept 50 days after last
        // download; an account (plain form sign-in, no captcha) raises both to 200 MB and 120 days and
        // changes nothing else — it only puts a session cookie on the same three steps. ⚠ It also
        // inspects INSIDE archives (200 MB per unpacked file, 400 MB total, and no more than 5x
        // expansion above 50 MB). See UploadEePipeline.cs.
        { "Upload.ee", "upload.ee" },
        // UpZur — ANONYMOUS. Stock XFileSharing, 200 MB, no extension restrictions, verified with real
        // bytes 2026-08-06 (the candidate list that suggested it said "Sign-Up Required" — it is not).
        // ⚠ Its homepage renders NO upload form, so the base's scrape finds nothing; the node comes
        // from ?op=api_get_limits, which also carries the cap. See UpZurPipeline.cs.
        { "UpZur", "upzur.com" },
        // GigaFile (ギガファイル便) — ANONYMOUS, and the largest per-file allowance here: 300 GB kept
        // 100 days. No accounts exist. Protocol came from the site's own js/upload.js: the homepage
        // declares a ROTATING node (var server = "NNN.gigafile.nu"), then one multipart POST per
        // 100 MB chunk to /upload_chunk.php with id/name/chunk/chunks/lifetime, and the LAST chunk's
        // reply carries the url + delkey. ⚠ lifetime defaults to 7 days in their page — we send 100,
        // the longest their slider offers. See GigaFilePipeline.cs.
        { "GigaFile", "gigafile.nu" },
        { "Easybytez", "easybytez.org" },
        { "Filedot", "filedot.to" },
        // terabytez.org — XFileSharing, ACCOUNT-only (anonymous classic post → 500 "Uploads not
        // enabled for this type of users"; its put_chunk.cgi takes the bytes and import_file then
        // refuses). NO REST API at all — /api/upload/server 404s — so sign-in is the only route.
        // Stock web form: the file form carries its own upload.cgi action. 100 MB/file free,
        // storage advertised unlimited. See TeraBytezPipeline.cs.
        { "TeraBytez", "terabytez.org" },
        // datavaults.co — XFileSharing on the REST API path (/api/upload/server → upload.cgi), which
        // this host DOCUMENTS at /pages/api and issues keys for from My Account ("Generate API Key" —
        // the base's existing generate step drives it). ACCOUNT-only. 5 GB/file, storage unlimited
        // (storage_left "inf"). ⚠ Its anonymous upload.cgi answers file_status OK with file_code
        // "undef" — a success shape for a discarded upload; the base rejects "undef" because of it.
        // ⚠ Its origin serves exactly FOUR concurrent API requests and 520s the rest, so uploads are
        // capped at 4. See DataVaultsPipeline.cs.
        { "DataVaults", "datavaults.co" },
        // ShareMods DISABLED 2026-08-02, the day it was written. It IS anonymous and the upload was
        // verified with real bytes (two guest uploads, both link pages served, 200 MB/file) — but
        // Cloudflare then began answering every .NET request with a managed challenge and had not
        // relented after a cooldown, while other clients still got 200s. Permanent block vs.
        // reputation earned by probing was never resolved. The pipeline is complete and tested;
        // re-enable only after a clean address completes an upload. See SharemodsPipeline.cs.
        // { "ShareMods", "sharemods.com" },
        // dropmefiles.com — anonymous, no login. Scrape SERVERID → upload/create (a drop uid) → 4 MB
        // chunks over the resumable nginx protocol (Session-ID + Content-Range + Content-Disposition;
        // WITHOUT those it 415s) → upload/save. Link is dropmefiles.com/<uid> and EXPIRES in 14 days.
        // Serialised to one upload at a time — its anti-abuse answers bursts with "Spam". See
        // DropMeFilesPipeline.cs.
        { "DropMeFiles", "dropmefiles.com" },
        // sendspace.com — anonymous, no login and no captcha: scrape the homepage's rotating upload
        // ticket (fsNNu node + signature) → multipart POST `upload_file[]` → the reply IS the result
        // page, carrying sendspace.com/file/<code>. 300 MB. See SendspacePipeline.cs.
        { "Sendspace", "www.sendspace.com" },
        // webshare.cz — anonymous, via the site's own plupload uploader: POST /api/upload_url/ (keyless,
        // XML) → node, then multipart `file` + wst=""/folder/private/adult/total/offset/name → {"ident"}.
        // Chunked at 1 GiB, threading the ident. Link is webshare.cz/file/<ident>/<slug> — the plain
        // path, NOT the site's own "#/" variant, which only a browser can resolve. See WebsharePipeline.cs.
        { "Webshare", "webshare.cz" },
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
