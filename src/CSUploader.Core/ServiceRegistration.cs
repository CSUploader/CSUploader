// <copyright file="ServiceRegistration.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader;

/// <summary>
/// Framework-free composition root for the shared core. Both the WPF head and the coming
/// Avalonia head call <see cref="AddCoreServices"/> for everything that has no UI dependency,
/// then add their own UI services (IDialogService, IInteractiveAuthService, tray, toasts,
/// view-models) on top. <see cref="WireRuntime"/> performs the eager startup wiring both
/// heads need once the provider is built.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers every UI-agnostic service the app needs: logging, settings, EF Core, the DAL
    /// repositories, the upload scheduler/pipeline graph (including all file-hoster pipelines),
    /// networking, the update service, and the upload-notification listener. Heads add their
    /// own IDialogService/IInteractiveAuthService/tray/toast/view-model registrations afterward.
    /// </summary>
    public static IServiceCollection AddCoreServices(this IServiceCollection services, string baseDirectory)
    {
        // Logging
        Logger logger = new();
        Logger.Current = logger;
        services.AddSingleton<IAppLogger>(logger);

        // App Settings
        AppSettings appSettings = new();
        services.AddSingleton(appSettings);

        // EF Core. Windows keeps the shipped v1.0.0 location (beside the executable); a packaged
        // non-Windows app runs from a read-only AppImage mount where SQLite can't create the file
        // (Error 14), so the DB moves to the per-user data dir. Create the parent either way — a
        // no-op on Windows (baseDirectory already exists), the actual fix on Linux/macOS. See AppDataPaths.
        string dbPath = Lib.AppDataPaths.ComposeDbPath(
            OperatingSystem.IsWindows(), baseDirectory, Lib.AppDataPaths.ResolveLocalAppData());
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddDbContextFactory<CSUploaderDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // DAL - Repositories
        services.AddSingleton<SettingRepository>();
        services.AddSingleton<FileHosterLoginRepository>();
        services.AddSingleton<UploadPackageRepository>();
        services.AddSingleton<UploadPackageFileRepository>();
        services.AddSingleton<ProxySettingRepository>();
        services.AddSingleton<LogEntryRepository>();

        // Upload
        services.AddSingleton<Lib.Crypto.IHashingService, Lib.Crypto.HashingService>();
        services.AddSingleton<UploadScheduler>();
        services.AddSingleton<PackageManager>();

        // Networking
        services.AddSingleton<Lib.Net.ProxyManager>();

        // Pipeline (upload hot path wiring)
        services.AddSingleton<Lib.Net.IProxySource>(sp => sp.GetRequiredService<Lib.Net.ProxyManager>());
        services.AddSingleton<Lib.Net.Http.IHttpHandlerFactory>(sp => new Lib.Net.Http.DefaultHttpHandlerFactory(sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<Upload.Pipeline.IFileHosterRegistry>(sp => new Upload.Pipeline.DefaultFileHosterRegistry(sp.GetServices<Upload.Pipeline.IFileHosterPipeline>()));
        services.AddSingleton<Upload.Pipeline.AttemptRunner>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.RapidgatorPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.AlfafilePipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.BRuploadPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.ExLoadPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.FileBoomPipeline(
                sp.GetRequiredService<IInteractiveAuthService>(),
                sp.GetRequiredService<FileHosterLoginRepository>()));
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.KatFilePipeline>();
        // FlashBit DISABLED 2026-06-05. The storage subdomain (fs1.flashbit.cc) ships
        // an invalid/expired SSL cert; the HTTPS→HTTP scheme-downgrade workaround in
        // commit 725ffba got past the TLS handshake, but then their Microsoft-IIS/10.0
        // backend rejects both our chunked and classic upload bodies via its tight
        // maxAllowedContentLength cap (even the 20 MiB probe-and-shrink retry 413s in
        // some cases). Re-enable ONLY after verifying both (a) FlashBit reissues a
        // valid cert for fs*.flashbit.cc AND (b) their backend accepts the XFS upload
        // protocol shapes we already implement. See FlashBitPipeline.cs class-level
        // remarks for the full diagnosis chain.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FlashBitPipeline>();
        // TakeFile DISABLED 2026-06-28. takefile.link's whole domain sits behind a Cloudflare
        // *managed* challenge: the C# my_account scrape gets the "Just a moment…" interstitial.
        // We implemented + tested cf_clearance forwarding (capture the WebView's clearance, pin the
        // UA, forward both cookies) but a managed challenge ALSO validates the browser TLS
        // fingerprint, which a .NET HttpClient can't reproduce — so even a valid clearance + matching
        // UA + IP is rejected. Same wall as ExtMatrix/Hotlink/FlashBit. The pipeline (incl. the
        // cf_clearance overrides + /user_login login path) is retained; re-enable only if TakeFile
        // drops the managed challenge. See TakeFilePipeline.cs class-level remarks.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TakeFilePipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HexloadPipeline>();
        // UsersDrive — classic XFS with a live ANONYMOUS upload (verified by uploading a file, not just
        // by the form rendering). 5250 MB guest cap. See UsersDrivePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UsersDrivePipeline>();
        // Filestank — YetiShare (NOT XFileSharing), on the session-cookie path: WebView sign-in for the
        // filehosting cookie → per-upload uploader.js scrape → blueimp multipart to a strN. node. DI
        // fills its IInteractiveAuthService + repo. See FilestankPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FilestankPipeline>();
        // Send.now / Uploady are stock XFileSharing hosters wired as thin shims on the shared base, so
        // both take the same auth service + repo as their siblings. They land on opposite paths: Send.now
        // uploads anonymously (verified live 2026-07-26), while Uploady is account-only on the web-form
        // path — its anonymous upload fails server-side, in a real browser too (capture 2026-07-27).
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.SendNowPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadyPipeline>();
        // DropGalaxy DISABLED 2026-07-26 — anonymous cap is 0.00001 MB (~10 bytes) and registration
        // is closed, so neither upload path is usable. Registry entry + ApiKeyHosters entry are
        // commented out alongside this. See DropGalaxyPipeline.cs for the diagnosis + re-enable steps.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DropGalaxyPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HxfilePipeline>();
        // IcerBox is a clean JSON REST API (email+password login → Bearer JWT → blueimp upload),
        // so it needs no IInteractiveAuthService — unlike the captcha-gated XFS/FileBoom/HitFile hosters.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.IcerBoxPipeline>();
        // Isracloud is a classic XFileSharing host that DOESN'T expose the REST API (no api-url on
        // my_account), so it runs the base's web-form path: WebView sign-in for the xfss cookie →
        // ?op=upload_form scrape → classic upload.cgi. DI fills its IInteractiveAuthService + repo.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.IsraCloudPipeline>();
        // Keep2Share is FileBoom's "moneyplatform" sister site (identical /v1 API) — WebView
        // accessToken sign-in, so it needs IInteractiveAuthService + the repo like FileBoom.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.Keep2SharePipeline(
                sp.GetRequiredService<IInteractiveAuthService>(),
                sp.GetRequiredService<FileHosterLoginRepository>()));
        // TezFiles — third moneyplatform sister (same /v1 API as FileBoom/Keep2Share); WebView
        // accessToken sign-in, so it needs IInteractiveAuthService + the repo like the others.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.TezFilesPipeline(
                sp.GetRequiredService<IInteractiveAuthService>(),
                sp.GetRequiredService<FileHosterLoginRepository>()));
        // Hotlink DISABLED 2026-06-23. hotlink.cc free accounts can't upload (op=upload →
        // "You are not allowed to upload files"; uploading is premium-only) AND its XFileSharing
        // Pro per-user API key is never rendered on my_account, so the api-key bootstrap is
        // structurally impossible. Re-enabling also needs a different (logged-in web) upload path
        // than the rest of the XFS-API family. See HotlinkPipeline.cs class-level remarks for the
        // full diagnosis + re-enable checklist.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HotlinkPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.GigaPetaPipeline>();
        // 1Fichier — anonymous only (no auth service needed): homepage scrape for a rotating node +
        // per-upload session id, one multipart POST, then the result page named by the 302. See
        // OneFichierPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.OneFichierPipeline>();
        // VikingFile — anonymous only (no auth service needed): its own documented API, get-upload-url
        // → presigned R2 part PUTs → complete-upload. See VikingFilePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.VikingFilePipeline>();
        // DailyUploads — ANONYMOUS xfspro chunked upload, sharing XfsProAnonymousPipeline with FILEAXA.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DailyUploadsPipeline>();
        // FILEAXA — ANONYMOUS xfspro chunked upload (no auth service needed): GET /server → put_chunk.cgi
        // → multipart api.cgi with an empty sess_id. See FileaxaPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FileaxaPipeline>();
        // Uploadrar — classic XFileSharing on the API-key path, so it needs the same auth service +
        // repo as its siblings. Account-only, and it blocks common media extensions (checked before
        // upload — the host itself only rejects them after taking the file). See UploadrarPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadrarPipeline>();
        // filedot.to — XFileSharing on the web-form (sign-in) path: its REST API works but publishes no
        // key anywhere a user can reach. Account-only, and it blocks image extensions (checked before
        // upload). Takes its node from GET /server, not the page's form action. See FiledotPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FiledotPipeline>();
        // TeraBytez — XFileSharing on the web-form (sign-in) path; it has no REST API at all
        // (/api/upload/server 404s). Account-only, 100 MB per file on a free account.
        // See TeraBytezPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TeraBytezPipeline>();
        // Data Vaults — XFileSharing on the REST API path; it documents that API and issues keys from
        // My Account, so the base derives one at sign-in. Account-only, 5 GB/file, storage unlimited.
        // See DataVaultsPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DataVaultsPipeline>();
        // Easybytez — xfspro chunked with a session, sharing XfsProSessionPipeline with filehoster.io.
        // Account-only (its guest form is decoration — the node refuses anonymous). 200 MB registered.
        // See EasybytezPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.EasybytezPipeline>();
        // EliteFile — the most stock XFileSharing host in the tree: family default routes, family
        // scrape. Account-only (no API at all). Its upload response names a DIFFERENT link domain.
        // See EliteFilePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.EliteFilePipeline>();
        // temp.sh — ANONYMOUS, no accounts: one multipart POST, the body is the link. 4 GB, ⚠ 3-day
        // expiry. See TempShPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TempShPipeline>();
        // Litterbox — catbox.moe's temporary sibling, same API shape plus a `time` field. ANONYMOUS,
        // 1 GB, ⚠ 72-hour maximum retention. See LitterboxPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.LitterboxPipeline>();
        // tmpfiles.org — ANONYMOUS, documented API. 100 MB, ⚠ 48-hour maximum retention (its default is
        // ONE hour unless expire is sent). See TmpFilesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TmpFilesPipeline>();
        // qu.ax — ANONYMOUS and PERMANENT (expiry=-1; its form defaults to 30 days). 256 MB.
        // ⚠ Allowlist: .rar/.zip/.7z fine, .r00/.sfv/.nfo refused. See QuAxPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.QuAxPipeline>();
        // upload.ee — ANONYMOUS or ACCOUNT, and the first Uber-Uploader (Perl CGI) host here: the server
        // mints an upload id, the POST carries only upfile_0, and the result page holds the link.
        // 100 MB / 50 days anonymous; an account is the same three steps with a session cookie on them,
        // for 200 MB / 120 days. See UploadEePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadEePipeline>();
        // UpZur — ANONYMOUS stock XFileSharing, 200 MB. Its homepage renders no upload form, so the
        // node comes from ?op=api_get_limits instead of a scrape. See UpZurPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UpZurPipeline>();
        // GigaFile — ANONYMOUS, no accounts at all. 300 GB per file kept 100 days: the largest
        // allowance here. Chunked multipart to a rotating node read off the homepage. See GigaFilePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.GigaFilePipeline>();
        // udrop — YetiShare with a GUEST upload (5 GiB, permanent). Same platform as Filestank, which
        // refuses guests; the difference is read from the script's own per-session cap. See UdropPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UdropPipeline>();
        // BowFile — udrop's sibling on YetiShare: guest upload, 20 GiB, but a SEPARATE fsNN. storage
        // node (so no cookie on the upload). See BowFilePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.BowFilePipeline>();
        // MegaUp — third guest host on YetiShare: 5 GiB, and BowFile's shape (separate mupload.store
        // node, so no cookie on the upload). See MegaUpPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.MegaUpPipeline>();
        // UploadNow — ANONYMOUS (its accounts are paid-only, so none are offered). Firebase anonymous
        // identity -> folder + file declared -> R2 multipart signed by the host's own signer. 100 GB.
        // See UploadNowPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadNowPipeline>();
        // Filebin — ANONYMOUS, no accounts, one POST per file to a random bin. ⚠ A bin is a PUBLIC
        // namespace, so each upload gets its own unguessable one. 7-day expiry. See FilebinPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FilebinPipeline>();
        // UploadHive — ANONYMOUS classic XFS, no stated size cap. Its form is on /upload (the homepage
        // has none) and the file form has no action, so the node is derived from the URL form's.
        // ⚠ Blocks .7z and .001. See UploadHivePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadHivePipeline>();
        // FileMirage — ANONYMOUS, 50 GiB, chunked: GET /api/servers for a node, then 99 MB multipart
        // chunks to <node>/upload.php; the last one carries the link. See FileMiragePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FileMiragePipeline>();

        // Filego — ANONYMOUS, 2 GB, three calls: /api/upload/init for an id + write token, a raw PUT of
        // the bytes, then /api/upload/save to commit. See FilegoPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FilegoPipeline>();

        // DropMB — a Pingvin Share instance: create share -> chunked octet-stream POSTs -> complete.
        // Anonymous or signed in (one access_token cookie). See DropMbPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DropMbPipeline>();

        // Hostize — ANONYMOUS, 20 GB, presigned S3 multipart (the storage.to / VikingFile shape).
        // ⚠ Free links live 24 HOURS. See HostizePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HostizePipeline>();

        // FileCat — ACCOUNT-ONLY, a small JSON API on api.filecat.net: signin -> upldreq -> one
        // multipart POST to the node it names. 2000 MiB per file. See FileCatPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FileCatPipeline>();

        // DataNodes — xfspro chunked, anonymous OR account on one path (only sess_id differs).
        // put_chunk_mt.cgi with X-Seek-To; 3 GiB. See DataNodesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DataNodesPipeline>();

        // BtaFile — stock XFS web form, anonymous 100 MB or 10 GB signed in; no REST API, and its
        // upload form is on ?op=upload rather than ?op=upload_form. See BtaFilePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.BtaFilePipeline>();

        // DepositFiles — ACCOUNT-ONLY, 10 GiB, small JSON API. Its login is a plain password post
        // until the host asks for a captcha, which is why it takes the auth service. See
        // DepositFilesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DepositFilesPipeline>();

        // Emload — ACCOUNT-ONLY SPA API; four cookies authenticate it and the node call pre-flights
        // the size against the account's remaining disk. See EmloadPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.EmloadPipeline>();

        // kshared — ACCOUNT-ONLY, Emload's engine in a /v1/ dialect. Three tokens from one sign-in,
        // each used somewhere different. See KsharedPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.KsharedPipeline>();

        // PreFiles — stock XFS web form, ACCOUNT-ONLY, 512 MB. Rewritten login routes; everything
        // else is the family default. See PrefilesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.PrefilesPipeline>();

        // Xubster — classic XFS, ANONYMOUS at 10 MB / 500 MB signed in, on nodes that rotate across
        // hosts AND ports on a different domain (xubster.ink). Upload page is ?op=upload, and it
        // publishes an extension blocklist. See XubsterPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.XubsterPipeline>();

        // World Files — classic XFS with a LIVE anonymous upload (5 GB guest / 10 GB account), even
        // though the site renders no guest form: the node comes from ?op=api_get_limits, as on UpZur
        // and BtaFile. See WorldFilesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.WorldFilesPipeline>();

        // UploadGIG — ACCOUNT-ONLY, on the host's own published two-call API. Serialised to one upload
        // at a time: each needs a 60-second address, and asking for one is a rate-limited login.
        // See UploadGigPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UploadGigPipeline>();

        // SubyShare — ACCOUNT-ONLY XFS of an older vintage, 5 GB, free accounts included. Its form
        // action is half-built (the page's script appends the upload id), its field set carries the
        // account's usr_id, and its reply is HTML rather than JSON. See SubysharePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.SubysharePipeline>();

        // FileStore — ACCOUNT-ONLY XFS whose APEX is Cloudflare-challenged to this client while its
        // upload nodes are not; the sign-in browser fetches the form and hands back both the node and
        // the session. Takes the auth service for that. See FileStorePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.FileStorePipeline(sp.GetRequiredService<IInteractiveAuthService>()));

        // ShareMods DISABLED 2026-08-02, the day it was written — not because the upload failed (two
        // anonymous uploads were verified with real bytes) but because Cloudflare began answering
        // every .NET request with a managed challenge and had not relented after a cooldown, while
        // other clients kept getting 200s. Whether that is permanent or reputation earned by probing
        // was never resolved, and shipping a host that might greet users with a challenge is worse
        // than not offering it. Re-enable ONLY after a clean address completes an upload — the
        // pipeline itself is finished and tested. See SharemodsPipeline.cs class-level remarks.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.SharemodsPipeline>();
        // DropMeFiles — anonymous only, and deliberately serialised (its anti-abuse answers bursts with
        // "Spam"). Resumable nginx chunk protocol; links expire in 14 days. See DropMeFilesPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DropMeFilesPipeline>();
        // Sendspace — anonymous only (no auth service needed): scrape the homepage's rotating upload
        // ticket, then post the site's own form to it. See SendspacePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.SendspacePipeline>();
        // Webshare — anonymous only (no auth service needed): keyless /api/upload_url/ node lookup, then
        // the site's own plupload multipart with an empty wst. See WebsharePipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.WebsharePipeline>();
        // Clicknupload — stock XFileSharing on the web-form path; account-only (its anonymous upload
        // is refused with "uploads are not enabled for your account type"). See ClicknuploadPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.ClicknuploadPipeline>();
        // DDownload — XFileSharing Pro on the standard API-key path; its API answers only on
        // api-v2.ddownload.com. See DDownloadPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.DDownloadPipeline>();
        // transfer.it (MEGA backend) — anonymous, no auth service needed.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TransferItPipeline>();
        // mega.nz — account upload into a Cloud Drive over the same MEGA protocol (password login + node verbs).
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.MegaPipeline>();
        // MediaFire — account-only REST upload: web login (user cookie) → session token → SHA-256
        // hash-dedup check → instant link or raw simple.php byte upload + poll. No captcha/WebView.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.MediaFirePipeline>();
        // Pixeldrain — account-only: login (pd_auth_key cookie) → PUT /api/file/<name> raw body → {"id":...}.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.PixeldrainPipeline>();
        // File Garden — account-only: login (api.filegarden.com/token → auth cookie + userId) → raw POST
        // /users/<userId>/pipe with an X-Data metadata header → {"id","path"}. No captcha on login.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FileGardenPipeline>();
        // ufile.io — anonymous OR registered chunked upload (GET csrf/session → select_storage →
        // create_session → 99 MB chunks → finalise). Registered adds x-api-key + a dashboard finalise;
        // the api_key comes from a reCAPTCHA WebView sign-in, so it needs IInteractiveAuthService.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.UfileIoPipeline(sp.GetRequiredService<IInteractiveAuthService>()));
        // Upstore — anonymous-only Dropzone upload (no login), same standalone shape as GigaPeta.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.UpstorePipeline>();
        // catbox.moe — anonymous-only single multipart POST to /user/api.php; response is the plain URL.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.CatboxPipeline>();
        // Buzzheavier — anonymous OR account via the developer API (raw PUT to w.buzzheavier.com/<name>).
        // Account auth is a Bearer of the account id captured via a Cloudflare-Turnstile WebView sign-in,
        // so it needs IInteractiveAuthService.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.BuzzheavierPipeline(
                sp.GetRequiredService<IInteractiveAuthService>()));
        // gofile.io — anonymous guest upload (create account → folder → multipart uploadfile).
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.GofilePipeline>();
        // storage.to — anonymous-only presigned-R2 upload (init-batch → PUT → confirm-batch), no login.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.StorageToPipeline>();
        // filehoster.io — account-only XFileSharing "xfspro" chunked upload (login → start_upload → put_chunk → import_file).
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.FilehosterIoPipeline>();
        // wormhole.app — anonymous WebTorrent + RFC 8188 E2E + Backblaze B2 (room → encrypt → manifest → B2 → finish).
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.WormholePipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.HitFilePipeline(
                sp.GetRequiredService<IInteractiveAuthService>()));
        // Turbobit — HitFile's sibling (same operator, same SPA platform, apptype fd1 not fd2);
        // account-only, WebView sign-in yields the durable appId. See TurbobitPipeline.cs.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.TurbobitPipeline(
                sp.GetRequiredService<IInteractiveAuthService>()));
        // NitroFlare: reCAPTCHA-gated WebView sign-in yields a durable 40-hex upload hash (HitFile
        // shape), so it needs IInteractiveAuthService.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.NitroFlarePipeline(
                sp.GetRequiredService<IInteractiveAuthService>()));
        // ExtMatrix DISABLED 2026-06-07. Their /api/upload.php endpoint hits the origin
        // nginx's client_max_body_size cap below ~27 MiB (clean 413 Payload Too Large
        // back from nginx, fronted by Cloudflare). Their web UI works around this with a
        // chunked upload protocol, but: (a) the protocol is undocumented (the public
        // /api/docs.php only describes the simple single-POST endpoint), and (b) the
        // live web UI is currently also failing so we can't capture the chunked wire
        // shape to reverse-engineer it. Re-enable only after ExtMatrix either raises
        // the API endpoint's body cap or after we capture a successful web-UI upload
        // and land a chunked protocol implementation. See ExtMatrixPipeline.cs
        // class-level remarks for the full re-enable checklist.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.ExtMatrixPipeline>();
        services.AddSingleton<IAccountVerifier, AccountVerifier>();

        // Services (framework-free)
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<UploadNotificationListener>();

        // ViewModels. Now framework-free, so both heads share these registrations; each head
        // supplies its own implementations of the UI interfaces the VMs depend on (IDialogService,
        // IUiDispatcher, IClipboardService, IThemeApplier, ITrayIconService, IFontEnumerationService,
        // IUpdateProgressSink), which is why those registrations stay head-side.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<UploadsViewModel>();
        services.AddSingleton<UploadedViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ConnectionManagerViewModel>();
        services.AddSingleton<LogsViewModel>();

        // Transient: a fresh wizard per open (unlike the singleton shell VMs). Both heads' UploadWizardWindow
        // resolve this instead of hand-building it (Phase 9 ledger fix d). The two optional ctor args
        // (IFileHosterRegistry, IAccountVerifier) are registered above, so DI injects the real graph.
        services.AddTransient<UploadWizardViewModel>();

        return services;
    }

    /// <summary>
    /// Performs the eager startup wiring both heads need once the provider is built: bridges the
    /// upload pipeline's AttemptCompleted event into ProxyManager.ReportResult, and eagerly resolves
    /// <see cref="UploadNotificationListener"/> so it subscribes to
    /// <c>UploadScheduler.FileStateChanged</c> immediately (singletons are otherwise lazy).
    /// </summary>
    public static void WireRuntime(IServiceProvider provider)
    {
        // Pipeline → ProxyManager bridge: AttemptCompleted feeds ProxyResultObserved.
        Lib.Net.ProxyManager proxyManager = provider.GetRequiredService<Lib.Net.ProxyManager>();
        Upload.Pipeline.AttemptRunner runner = provider.GetRequiredService<Upload.Pipeline.AttemptRunner>();
        runner.AttemptCompleted += (_, completed) =>
        {
            if (completed.ProxyId > 0)
            {
                proxyManager.ReportResult(completed.ProxyId, completed.Success);
            }
        };

        // Eagerly resolve the notification listener so it subscribes to
        // UploadScheduler.FileStateChanged immediately at startup (singletons are lazy).
        _ = provider.GetRequiredService<UploadNotificationListener>();
    }
}
