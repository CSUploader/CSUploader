// <copyright file="AvaloniaStartupDISmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using Avalonia.Headless.XUnit;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Smoke test for the Avalonia head's DI graph. Builds the same <see cref="ServiceProvider"/> the
/// head composes at startup (<c>App.ConfigureServices</c>, internal — reachable through the head's
/// <c>InternalsVisibleTo</c>) and resolves every head registration, the Core upload graph, and all six
/// shared ViewModels, then runs <c>ServiceRegistration.WireRuntime</c>. This is the Avalonia sibling
/// of the WPF <c>StartupDISmokeTests</c>: it proves the Avalonia UI-interface implementations satisfy
/// the Core graph the shared ViewModels depend on.
/// </summary>
/// <remarks>
/// <para>
/// Runs under <see cref="AvaloniaFactAttribute"/> (headless UI thread) and resolves <b>inline</b> — no
/// <c>Task.Run</c> watchdog like the WPF smoke uses. The ViewModel constructors call
/// <c>IUiDispatcher.CreateTimer(...)</c>, and <see cref="global::CSUploader.Services.AvaloniaUiDispatcher"/>
/// creates a real Avalonia <c>DispatcherTimer</c>, which reads <c>Dispatcher.UIThread</c>; resolving off
/// the UI thread would break the very construction this smoke exists to exercise.
/// </para>
/// <para>
/// A circular DI factory would manifest here as a hang rather than a fast failure (the WPF smoke's
/// timeout catches that case for the shared Core graph already, in the WPF suite); the value this test
/// adds is confirming the Avalonia head's own registrations complete the graph.
/// </para>
/// </remarks>
public class AvaloniaStartupDISmokeTests
{
    /// <summary>
    /// Velopack's locator is a static initialised by <c>VelopackApp.Build().Run()</c> and queried by
    /// <c>UpdateService</c>'s constructor (reached transitively through <c>MainViewModel</c>).
    /// Initialise it once for the whole assembly so resolution doesn't throw
    /// "No VelopackLocator has been set". Calling it more than once is a no-op in practice.
    /// </summary>
    private static readonly object _velopackInit = InitVelopack();

    private static object InitVelopack()
    {
        VelopackApp.Build().Run();
        return new();
    }

    [AvaloniaFact]
    public void ConfigureServices_ResolvesAllHeadRegistrationsAndViewModels()
    {
        _ = _velopackInit; // ensure the static initialiser ran before resolution

        // A writable base directory holding the (lazily-opened) SQLite file, mirroring what the head's
        // OnFrameworkInitializationCompleted passes. The factory is lazy, so nothing opens the DB during
        // resolution and no migration runs.
        string tempDir = Path.Combine(Path.GetTempPath(), "csu-ava-startup-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ServiceCollection services = new();
            App.ConfigureServices(services, tempDir);
            using ServiceProvider provider = services.BuildServiceProvider();

            // The seven head-supplied UI interfaces the shared ViewModels depend on, plus the two extra
            // head registrations the Core graph needs (IInteractiveAuthService feeds the captcha-gated
            // pipelines; IToastNotificationService feeds UploadNotificationListener). This is the
            // "all head registrations present" gate — mirrors tests/StartupDISmokeTests.cs:138-148.
            Assert.NotNull(provider.GetRequiredService<IDialogService>());
            Assert.NotNull(provider.GetRequiredService<IUiDispatcher>());
            Assert.NotNull(provider.GetRequiredService<IClipboardService>());
            Assert.NotNull(provider.GetRequiredService<IThemeApplier>());
            Assert.NotNull(provider.GetRequiredService<ITrayIconService>());
            Assert.NotNull(provider.GetRequiredService<IFontEnumerationService>());
            Assert.NotNull(provider.GetRequiredService<IUpdateProgressSink>());
            Assert.NotNull(provider.GetRequiredService<IInteractiveAuthService>());
            Assert.NotNull(provider.GetRequiredService<IToastNotificationService>());

            // Core upload graph resolves and at least one hoster pipeline is registered.
            Assert.NotNull(provider.GetRequiredService<PackageManager>());
            Assert.NotNull(provider.GetRequiredService<UploadScheduler>());
            Assert.NotNull(provider.GetRequiredService<AttemptRunner>());
            Assert.NotEmpty(provider.GetServices<IFileHosterPipeline>());

            // All six shared ViewModels resolve (their ctors create dispatcher timers — the reason this
            // runs on the headless UI thread). MainViewModel cascades into the other five.
            Assert.NotNull(provider.GetRequiredService<MainViewModel>());
            Assert.NotNull(provider.GetRequiredService<UploadsViewModel>());
            Assert.NotNull(provider.GetRequiredService<UploadedViewModel>());
            Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
            Assert.NotNull(provider.GetRequiredService<ConnectionManagerViewModel>());
            Assert.NotNull(provider.GetRequiredService<LogsViewModel>());

            ServiceRegistration.WireRuntime(provider); // must not throw
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* leave the temp tree if Windows still holds a handle; cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// Phase 9 ledger fix (d): <see cref="UploadWizardViewModel"/> is DI-registered (Transient), so both heads
    /// resolve it from the container instead of hand-building the seven-arg ctor in the wizard window's
    /// code-behind. Transient means a fresh wizard per open — two resolutions must be distinct instances.
    /// Runs under <see cref="AvaloniaFactAttribute"/> for the same reason as the sibling smoke: resolution
    /// cascades through the pipeline registry into the head's UI-thread-bound <c>IDialogService</c>.
    /// </summary>
    [AvaloniaFact]
    public void Provider_Resolves_UploadWizardViewModel_Transient()
    {
        _ = _velopackInit; // parity with the sibling smoke; the class static runs on first use regardless

        string tempDir = Path.Combine(Path.GetTempPath(), "csu-ava-wizard-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ServiceCollection services = new();
            App.ConfigureServices(services, tempDir);
            using ServiceProvider provider = services.BuildServiceProvider();

            UploadWizardViewModel a = provider.GetRequiredService<UploadWizardViewModel>();
            UploadWizardViewModel b = provider.GetRequiredService<UploadWizardViewModel>();

            Assert.NotNull(a);
            Assert.NotSame(a, b); // Transient: a fresh wizard per open.
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* leave the temp tree if Windows still holds a handle; cleanup is best-effort */ }
        }
    }
}
