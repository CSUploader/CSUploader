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
        _ = _serviceProvider.GetRequiredService<Services.UploadNotificationListener>();

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

    private static void ConfigureServices(IServiceCollection services, string baseDirectory)
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

        // Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<IToastWindowFactory, DefaultToastWindowFactory>();
        services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IToastWindowFactory>(),
            workAreaProvider: () => System.Windows.SystemParameters.WorkArea,
            activate: () => sp.GetRequiredService<ViewModels.MainViewModel>().ActivateAndShowUploadedTab(),
            dispatchToUi: action => System.Windows.Application.Current.Dispatcher.BeginInvoke(action)));
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
