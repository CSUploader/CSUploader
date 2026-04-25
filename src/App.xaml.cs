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
        AppSettings.Current = appSettings;
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

        // Upload
        services.AddSingleton<UploadScheduler>();
        services.AddSingleton<PackageManager>();

        // Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<UploadsViewModel>();
        services.AddSingleton<UploadedViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();
    }
}
