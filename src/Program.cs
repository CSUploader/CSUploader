// <copyright file="Program.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Upload;
using CSUploader.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader;

public static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    public static void Main()
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AppDomain.CurrentDomain.SetData("DataDirectory", baseDirectory);

        ServiceCollection services = new();
        ConfigureServices(services, baseDirectory);
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(serviceProvider));
    }

    private static void ConfigureServices(IServiceCollection services, string baseDirectory)
    {
        // Logging - single instance shared between DI and static accessor
        Logger logger = new();
        Logger.Current = logger;
        services.AddSingleton<IAppLogger>(logger);

        string dbPath = Path.Combine(baseDirectory, "CSUploader.db");

        // EF Core
        services.AddDbContextFactory<CSUploaderDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // DAL - Stores
        services.AddSingleton<SettingStore>();
        services.AddSingleton<FileHosterLoginStore>();
        services.AddSingleton<UploadPackageStore>();
        services.AddSingleton<UploadPackageFileStore>();

        // App Settings
        AppSettings appSettings = new();
        AppSettings.Current = appSettings;
        services.AddSingleton(appSettings);

        // DAL - Managers
        services.AddSingleton<SettingManager>();
        services.AddSingleton<FileHosterLoginManager>();
        services.AddSingleton<UploadPackageManager>();
        services.AddSingleton<UploadPackageFileManager>();

    }
}
