// <copyright file="StartupDISmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Tests.ViewModels;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Velopack;

namespace CSUploader.Tests;

/// <summary>
/// DI smoke tests for the shared Core composition root (<see cref="ServiceRegistration.AddCoreServices"/>) —
/// the graph both heads build on. Each test composes Core plus a test double for every interface a head
/// supplies, so the shared graph is exercised without either UI framework:
/// <list type="bullet">
///   <item><see cref="AddCoreServices_ResolvesCoreGraphWithoutUiRegistrations"/> is the Core/head boundary
///   gate — a genuinely head-only dependency leaking into a Core registration throws at resolve here, where
///   the full-head smoke (which supplies real UI implementations) would still pass.</item>
///   <item><see cref="BuildProviderAndResolveStartupGraph_DoesNotHangOrThrow"/> is a bounded watchdog over
///   the same graph — it fails on a hang, catching the opaque-factory cycle class described below.</item>
/// </list>
/// The Avalonia head's own registrations are covered by the headless sibling
/// <c>AvaloniaStartupDISmokeTests</c> in CSUploader.Tests.
/// </summary>
/// <remarks>
/// The watchdog exists because of a shipped cycle MS.DI's built-in detector missed: DialogService →
/// IAccountVerifier → IFileHosterRegistry → IFileHosterPipeline[] → ExLoadPipeline → IInteractiveAuthService
/// → WebViewInteractiveAuthService → IDialogService. MS.DI walked it forever instead of throwing because the
/// IFileHosterRegistry factory (which calls <c>sp.GetServices&lt;IFileHosterPipeline&gt;()</c> inside an
/// <c>AddSingleton(sp =&gt; …)</c> lambda — ServiceRegistration.cs) is opaque to the cycle detector. Symptom
/// in the wild was "process runs but no main window": the provider was still spinning when the window should
/// have shown. Fixed in commit 2474cd1 by lazily resolving the verifier inside the Sign-in lambda. That
/// opaque factory edge (IFileHosterRegistry) lives in the Core graph, so the watchdog guards this class of
/// regression here even though the historical closing edge ran through head implementations (mocked here).
/// </remarks>
public class StartupDISmokeTests
{
    /// <summary>
    /// Velopack's locator is a static initialised by <c>VelopackApp.Build().Run()</c> and queried by
    /// <c>UpdateService</c>'s constructor (reached transitively through <see cref="MainViewModel"/>).
    /// Initialise it once for the whole assembly so resolving IUpdateService doesn't throw
    /// "No VelopackLocator has been set". Calling it more than once is a no-op in practice.
    /// </summary>
    private static readonly object _velopackInit = InitVelopack();

    private static object InitVelopack()
    {
        VelopackApp.Build().Run();
        return new();
    }

    /// <summary>
    /// Guards the Core/head DI boundary: <see cref="ServiceRegistration.AddCoreServices"/> must build a
    /// complete, resolvable graph on its own, given only test doubles for the interfaces a head implements.
    /// Both heads lean on exactly this — Core services plus their own UI implementations. If a genuinely
    /// head-only dependency leaks into a Core registration, this fails where the full-head smoke (which
    /// supplies real UI implementations) would still pass. It also resolves the six shared ViewModels, so a
    /// VM constructor that gains a head-only dependency outside the documented UI-interface set is caught here.
    /// </summary>
    [Fact]
    public void AddCoreServices_ResolvesCoreGraphWithoutUiRegistrations()
    {
        _ = _velopackInit; // MainViewModel resolves IUpdateService, whose ctor queries the locator

        ServiceCollection services = BuildCoreServicesWithHeadDoubles(Path.GetTempPath());
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

    /// <summary>
    /// Bounded watchdog over the shared Core startup graph: a timeout fails the test if resolution loops,
    /// catching the opaque-factory circular dependency (see the class remarks) that
    /// <c>Microsoft.Extensions.DependencyInjection</c>'s built-in cycle detector treats as opaque and walks
    /// forever instead of throwing.
    /// </summary>
    [Fact]
    public async Task BuildProviderAndResolveStartupGraph_DoesNotHangOrThrow()
    {
        _ = _velopackInit; // ensure the static initialiser ran before resolution

        // A writable base directory holding the (lazily-opened) SQLite file; the factory is lazy so the DB
        // isn't touched during resolution and no migration runs.
        string tempDir = Path.Combine(Path.GetTempPath(), "csu-core-startup-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ServiceCollection services = BuildCoreServicesWithHeadDoubles(tempDir);

            var resolveTask = Task.Run(() =>
            {
                using ServiceProvider sp = services.BuildServiceProvider();

                // The chokepoints the live cycle first hit: the eager singletons plus MainViewModel, which
                // cascades through SettingsViewModel into IDialogService (where the cycle closed) and pulls
                // the IFileHosterRegistry opaque factory.
                _ = sp.GetRequiredService<ProxyManager>();
                _ = sp.GetRequiredService<AttemptRunner>();
                _ = sp.GetRequiredService<MainViewModel>();
                ServiceRegistration.WireRuntime(sp);
            });

            // 5 seconds is generously over honest resolution (the live app builds the graph in well under a
            // second). A timeout means a factory is recursing — the only realistic explanation for exceeding
            // it without throwing first.
            Task completed = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != resolveTask)
            {
                // Don't try to interrupt the background task — it's spinning through MS.DI's resolution stack
                // and will be reaped at process exit. Surfacing the timeout is what matters.
                Assert.Fail(
                    "Core DI graph did not finish resolving in 5 seconds — almost certainly a circular "
                    + "dependency through a factory lambda (e.g. sp.GetServices<T>() inside an AddSingleton "
                    + "factory closing the cycle, which MS.Extensions.DependencyInjection's cycle detector "
                    + "treats as opaque). See StartupDISmokeTests remarks for the canonical example.");
            }

            // Surface any exception thrown during construction (a fault completes the task before the delay).
            await resolveTask;
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* leave the temp tree if Windows still holds a handle; cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// Core registrations plus a stand-in for every interface a head supplies: IInteractiveAuthService
    /// (feeds the captcha-gated hoster pipelines) and IToastNotificationService (feeds
    /// UploadNotificationListener, resolved by WireRuntime), plus the seven UI interfaces the six shared
    /// ViewModels depend on. IUiDispatcher is a real <see cref="InlineUiDispatcher"/> — not a bare mock —
    /// because the VM ctors call CreateTimer(...).Start(), and a mock's CreateTimer would hand back a null
    /// IUiTimer and NRE in the constructor.
    /// </summary>
    private static ServiceCollection BuildCoreServicesWithHeadDoubles(string baseDirectory)
    {
        ServiceCollection services = new();
        services.AddCoreServices(baseDirectory);

        services.AddSingleton(Mock.Of<IInteractiveAuthService>());
        services.AddSingleton(Mock.Of<IToastNotificationService>());
        services.AddSingleton(Mock.Of<IDialogService>());
        services.AddSingleton<IUiDispatcher>(new InlineUiDispatcher());
        services.AddSingleton(Mock.Of<IClipboardService>());
        services.AddSingleton(Mock.Of<IThemeApplier>());
        services.AddSingleton(Mock.Of<ITrayIconService>());
        services.AddSingleton(Mock.Of<IFontEnumerationService>());
        services.AddSingleton(Mock.Of<IUpdateProgressSink>());

        return services;
    }
}
