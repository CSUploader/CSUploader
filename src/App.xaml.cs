// <copyright file="App.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace CSUploader;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public IServiceProvider Services => _serviceProvider ?? throw new InvalidOperationException("Services not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack first-frame hook: handles --veloapp-install / --veloapp-uninstall
        // command-line flags that the installer fires. Must run before anything else.
        VelopackApp.Build().Run();

        base.OnStartup(e);

        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AppDomain.CurrentDomain.SetData("DataDirectory", baseDirectory);

        ServiceCollection services = new();
        ConfigureServices(services, baseDirectory);
        _serviceProvider = services.BuildServiceProvider();

        // Pipeline → ProxyManager bridge: AttemptCompleted feeds ProxyResultObserved.
        Lib.Net.ProxyManager proxyManager = _serviceProvider.GetRequiredService<Lib.Net.ProxyManager>();
        Upload.Pipeline.AttemptRunner runner = _serviceProvider.GetRequiredService<Upload.Pipeline.AttemptRunner>();
        runner.AttemptCompleted += (_, completed) =>
        {
            if (completed.ProxyId > 0)
            {
                proxyManager.ReportResult(completed.ProxyId, completed.Success);
            }
        };

        // Eagerly resolve the notification listener so it subscribes to
        // UploadScheduler.FileStateChanged immediately at startup (singletons are lazy).
        _ = _serviceProvider.GetRequiredService<UploadNotificationListener>();

        // Register the global Window.Loaded handler so every window picks up the
        // dark title bar automatically. MainViewModel.InitializeAsync sets the
        // initial value once the persisted setting is read.
        Lib.UI.ImmersiveDarkMode.RegisterGlobalHandler();

        MainWindow mainWindow = new(_serviceProvider);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    // internal so StartupDISmokeTests can build the same provider the app does at startup
    // and verify the graph resolves without hanging (catches DI cycles like the one in
    // commit 2474cd1 that loop through sp.GetServices<T>() inside a factory).
    internal static void ConfigureServices(IServiceCollection services, string baseDirectory)
    {
        // Logging
        Logger logger = new();
        Logger.Current = logger;
        services.AddSingleton<IAppLogger>(logger);

        // App Settings
        AppSettings appSettings = new();
        services.AddSingleton(appSettings);

        // EF Core
        string dbPath = Path.Combine(baseDirectory, "CSUploader.db");
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

        // Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IInteractiveAuthService, WebViewInteractiveAuthService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<IToastWindowFactory, DefaultToastWindowFactory>();
        services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IToastWindowFactory>(),
            workAreaProvider: () => SystemParameters.WorkArea,
            activate: () => sp.GetRequiredService<MainViewModel>().ActivateAndShowUploadedTab(),
            dispatchToUi: action => Current?.Dispatcher.BeginInvoke(action)));
        services.AddSingleton<UploadNotificationListener>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<UploadsViewModel>();
        services.AddSingleton<UploadedViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ConnectionManagerViewModel>();
        services.AddSingleton<LogsViewModel>();
    }
}
