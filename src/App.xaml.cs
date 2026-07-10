// <copyright file="App.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
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

        ServiceCollection services = new();
        ConfigureServices(services, baseDirectory);
        _serviceProvider = services.BuildServiceProvider();

        // Bridge the pipeline into the proxy health tracker and eagerly resolve the upload
        // notification listener — shared with the Avalonia head, so it lives in Core.
        ServiceRegistration.WireRuntime(_serviceProvider);

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
        // Everything UI-agnostic (logging, settings, EF Core, DAL, upload pipeline + all
        // hoster pipelines, networking, update service, notification listener) lives in Core
        // so the Avalonia head can share it. The WPF-specific registrations follow below.
        services.AddCoreServices(baseDirectory);

        // UI services (WPF-specific implementations of Core interfaces)
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUpdateProgressSink, WpfUpdateProgressSink>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IUiShell, WpfUiShell>();
        services.AddSingleton<IFontEnumerationService, WpfFontEnumerationService>();
        services.AddSingleton<IThemeApplier, WpfThemeApplier>();
        services.AddSingleton<IInteractiveAuthService, WebViewInteractiveAuthService>();
        services.AddSingleton<TrayIconManager>();
        // Same singleton instance is reachable through the Core interface too, so the
        // shared ViewModels can depend on ITrayIconService instead of the WinForms type.
        services.AddSingleton<ITrayIconService>(sp => sp.GetRequiredService<TrayIconManager>());
        services.AddSingleton<IToastWindowFactory, DefaultToastWindowFactory>();
        services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IToastWindowFactory>(),
            workAreaProvider: () =>
            {
                Rect wa = SystemParameters.WorkArea;
                return new DipRect(wa.X, wa.Y, wa.Width, wa.Height);
            },
            activate: () => sp.GetRequiredService<MainViewModel>().ActivateAndShowUploadedTab(),
            dispatchToUi: sp.GetRequiredService<IUiDispatcher>().Post));

        // ViewModels
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<UploadsViewModel>();
        services.AddSingleton<UploadedViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ConnectionManagerViewModel>();
        services.AddSingleton<LogsViewModel>();
    }
}
