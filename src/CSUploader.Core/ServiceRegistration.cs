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
