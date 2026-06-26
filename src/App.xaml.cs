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
                sp.GetRequiredService<Services.IInteractiveAuthService>(),
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
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.TakeFilePipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HexloadPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HxfilePipeline>();
        // IcerBox is a clean JSON REST API (email+password login → Bearer JWT → blueimp upload),
        // so it needs no IInteractiveAuthService — unlike the captcha-gated XFS/FileBoom/HitFile hosters.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.IcerBoxPipeline>();
        // Isracloud is a classic XFileSharing host that DOESN'T expose the REST API (no api-url on
        // my_account), so it runs the base's web-form path: WebView sign-in for the xfss cookie →
        // ?op=upload_form scrape → classic upload.cgi. DI fills its IInteractiveAuthService + repo.
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.IsraCloudPipeline>();
        // Hotlink DISABLED 2026-06-23. hotlink.cc free accounts can't upload (op=upload →
        // "You are not allowed to upload files"; uploading is premium-only) AND its XFileSharing
        // Pro per-user API key is never rendered on my_account, so the api-key bootstrap is
        // structurally impossible. Re-enabling also needs a different (logged-in web) upload path
        // than the rest of the XFS-API family. See HotlinkPipeline.cs class-level remarks for the
        // full diagnosis + re-enable checklist.
        // services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.HotlinkPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.GigaPetaPipeline>();
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline>(sp =>
            new Upload.Pipeline.Hosters.HitFilePipeline(
                sp.GetRequiredService<Services.IInteractiveAuthService>()));
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
        services.AddSingleton<Upload.IAccountVerifier, Upload.AccountVerifier>();

        // Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IInteractiveAuthService, WebViewInteractiveAuthService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<IToastWindowFactory, DefaultToastWindowFactory>();
        services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IToastWindowFactory>(),
            workAreaProvider: () => System.Windows.SystemParameters.WorkArea,
            activate: () => sp.GetRequiredService<MainViewModel>().ActivateAndShowUploadedTab(),
            dispatchToUi: action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action)));
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
