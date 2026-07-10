// <copyright file="StartupDISmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Velopack;

namespace CSUploader.Tests;

/// <summary>
/// Watchdog smoke test for the production DI graph. Builds the same
/// <see cref="ServiceProvider"/> <see cref="App.OnStartup"/> builds, then resolves the
/// services the app pulls eagerly during startup plus <see cref="MainViewModel"/>
/// (which cascades through every other view-model). A timeout fails the test if any
/// part of the resolution loops — catching circular dependencies that
/// <c>Microsoft.Extensions.DependencyInjection</c>'s built-in cycle detector misses
/// because the closing edge runs through <c>sp.GetServices&lt;T&gt;()</c> inside an
/// <c>AddSingleton(sp =&gt; …)</c> factory.
/// </summary>
/// <remarks>
/// The original such cycle was DialogService → IAccountVerifier → IFileHosterRegistry
/// → IFileHosterPipeline[] → ExLoadPipeline → IInteractiveAuthService →
/// WebViewInteractiveAuthService → IDialogService. MS.DI walked it forever instead of
/// throwing because the IFileHosterRegistry factory at App.xaml.cs:106 (which calls
/// <c>sp.GetServices&lt;IFileHosterPipeline&gt;()</c>) is opaque to the cycle detector.
/// Symptom in the wild was "process runs but no main window" — the DI provider was
/// still spinning when <c>MainWindow.Show()</c> should have been called. Fixed in
/// commit 2474cd1 by lazily resolving the verifier inside the Sign-in lambda.
/// </remarks>
public class StartupDISmokeTests
{
    /// <summary>
    /// Velopack's locator is a static initialised by <c>VelopackApp.Build().Run()</c> and
    /// queried by <c>UpdateService</c>'s constructor. Initialise it once for the whole
    /// test assembly so resolving IUpdateService doesn't throw "No VelopackLocator has
    /// been set". Calling it more than once is a no-op in practice.
    /// </summary>
    private static readonly object _velopackInit = InitVelopack();
    private static object InitVelopack()
    {
        VelopackApp.Build().Run();
        return new();
    }

    [Fact]
    public async Task BuildProviderAndResolveStartupGraph_DoesNotHangOrThrow()
    {
        _ = _velopackInit; // ensure the static initialiser ran before resolution

        // Match the layout App.OnStartup sets up: a writable base directory holding the
        // SQLite file. The factory is lazy so the file isn't actually opened until first
        // use (after resolution), which means no migration needs to run for this test.
        string tempDir = Path.Combine(Path.GetTempPath(), "csu-startup-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ServiceCollection services = new();
            App.ConfigureServices(services, tempDir);

            var resolveTask = Task.Run(() =>
            {
                using ServiceProvider sp = services.BuildServiceProvider();

                // Mirror the eager resolutions in App.OnStartup (lines 42-54). These were the
                // first chokepoints when the cycle was live.
                _ = sp.GetRequiredService<ProxyManager>();
                _ = sp.GetRequiredService<AttemptRunner>();
                _ = sp.GetRequiredService<UploadNotificationListener>();

                // MainViewModel transitively resolves UploadsViewModel, UploadedViewModel,
                // SettingsViewModel, ConnectionManagerViewModel, LogsViewModel — and through
                // SettingsViewModel reaches IDialogService, which is where the cycle closed.
                // Resolving this is what would have caught the regression.
                _ = sp.GetRequiredService<MainViewModel>();
            });

            // 5 seconds is generously over honest startup (the live app brings up the window
            // in well under a second on this dev box). A timeout here means a factory is
            // recursing — the only realistic explanation for taking longer than this without
            // throwing first.
            Task completed = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != resolveTask)
            {
                // We deliberately don't try to interrupt the background task: it's spinning
                // through MS.DI's resolution stack and will eventually OOM or get reaped by
                // the test process exit. Surfacing the timeout is what matters.
                Assert.Fail(
                    "Production DI graph did not finish resolving in 5 seconds — almost certainly a circular "
                    + "dependency through a factory lambda (e.g. sp.GetServices<T>() inside an AddSingleton "
                    + "factory closing the cycle, which MS.Extensions.DependencyInjection's cycle detector "
                    + "treats as opaque). See StartupDISmokeTests.cs remarks for the canonical example.");
            }

            // Surfaces any exception thrown during construction (which would have aborted the
            // task with a fault before the timeout fired).
            await resolveTask;
        }
        finally
        {
            // Best-effort cleanup — the SQLite file may or may not exist depending on whether
            // resolution touched the DB context factory.
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* leave the temp tree if Windows still has a handle; cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// Guards the Core/head DI boundary: <see cref="ServiceRegistration.AddCoreServices"/> must
    /// build a complete, resolvable graph on its own, given only test doubles for the interfaces
    /// the head implements. The Avalonia head will lean on exactly this — Core services plus its
    /// own UI implementations. If a genuinely head-only dependency leaks into a Core registration,
    /// this fails where the full-head smoke test (which supplies real WPF implementations) would
    /// still pass. It also resolves the six shared ViewModels, so a VM constructor that gains a
    /// head-only dependency outside the documented UI-interface set is caught here.
    /// </summary>
    [Fact]
    public void AddCoreServices_ResolvesCoreGraphWithoutUiRegistrations()
    {
        _ = _velopackInit; // MainViewModel resolves IUpdateService, whose ctor queries the locator

        ServiceCollection services = new();
        services.AddCoreServices(Path.GetTempPath());

        // Stand in for every head-supplied interface the Core graph consumes. The Avalonia head
        // registers its own implementations of exactly these. IInteractiveAuthService feeds the
        // captcha-gated hoster pipelines and IToastNotificationService feeds UploadNotificationListener
        // (resolved by WireRuntime); the remainder are the UI interfaces the shared ViewModels depend
        // on (see ServiceRegistration's ViewModel comment).
        services.AddSingleton(Mock.Of<IInteractiveAuthService>());
        services.AddSingleton(Mock.Of<IToastNotificationService>());
        services.AddSingleton(Mock.Of<IDialogService>());
        // Real (inert without an Application) dispatcher, not a bare mock: the VM ctors call
        // CreateTimer(...).Start(), and a mock's CreateTimer would return a null IUiTimer and NRE.
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton(Mock.Of<IClipboardService>());
        services.AddSingleton(Mock.Of<IThemeApplier>());
        services.AddSingleton(Mock.Of<ITrayIconService>());
        services.AddSingleton(Mock.Of<IFontEnumerationService>());
        services.AddSingleton(Mock.Of<IUpdateProgressSink>());
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PackageManager>());
        Assert.NotNull(provider.GetRequiredService<UploadScheduler>());
        Assert.NotNull(provider.GetRequiredService<AttemptRunner>());
        Assert.NotEmpty(provider.GetServices<IFileHosterPipeline>());

        // The six shared ViewModels must resolve from Core + the head doubles alone.
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        Assert.NotNull(provider.GetRequiredService<UploadsViewModel>());
        Assert.NotNull(provider.GetRequiredService<UploadedViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
        Assert.NotNull(provider.GetRequiredService<ConnectionManagerViewModel>());
        Assert.NotNull(provider.GetRequiredService<LogsViewModel>());

        ServiceRegistration.WireRuntime(provider); // must not throw
    }
}
