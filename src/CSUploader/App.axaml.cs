using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
#if AVA_BRIDGE
using AvaDevBridge;
#endif

namespace CSUploader;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// The composed DI provider, exposed for the few code-behind sites that resolve a
    /// ViewModel from the container - currently <see cref="Views.UploadWizardWindow"/>, which
    /// mirrors the WPF head's <c>((App)Application.Current).Services</c>. Assigned in
    /// <see cref="OnFrameworkInitializationCompleted"/>; it is <see langword="null"/> under the headless
    /// test lifetime (that method only runs for a classic-desktop lifetime), so only production/gallery
    /// paths — which already require a live desktop — dereference it.
    /// </summary>
    internal IServiceProvider Services => _serviceProvider!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Bitmaps merge in code (no XAML form for keyed bitmap resources); geometries and the
        // theme dictionaries merge in App.axaml. Initialize (not OnFrameworkInitializationCompleted)
        // so the headless test session gets the identical resource surface — the latter is guarded
        // by IClassicDesktopStyleApplicationLifetime and never runs under test.
        Resources.MergedDictionaries.Add(BuildBitmapDictionary());
    }

    private static ResourceDictionary BuildBitmapDictionary()
    {
        ResourceDictionary dict = new();

        // Fully qualified: inside App, the bare identifier `Resources` resolves to the
        // Application.Resources property, not the CSUploader.Resources namespace.
        CSUploader.Resources.BitmapImageResources.MergeInto(dict);
        return dict;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Mirror App.xaml.cs:22-53. Velopack's first-frame hook already ran in Program.Main,
            // so composition starts straight at building the DI graph.
            ServiceCollection services = new();
            ConfigureServices(services, AppDomain.CurrentDomain.BaseDirectory);
            _serviceProvider = services.BuildServiceProvider();

            // Bridge the pipeline into the proxy health tracker and eagerly resolve the upload
            // notification listener — shared with the WPF head, so it lives in Core.
            ServiceRegistration.WireRuntime(_serviceProvider);

            // Agent-safety guard (design §The Avalonia head). --agent means a bridge/ava-drive
            // session is driving the head, so pending uploads must never auto-start. Two
            // belt-and-braces measures, applied BEFORE MainViewModel (and therefore
            // PackageManager, which starts the scheduler loop) is resolved for MainWindow's
            // DataContext below:
            //   1. Latch AutostartUploads to Never. The settings-load inside InitializeAsync
            //      would otherwise copy the persisted policy back over any plain assignment
            //      (SettingsViewModel's unconditional VM→settings write at load), so a latch —
            //      which wins over every later write while leaving the setter/persistence
            //      untouched — is required. This alone stops LoadPersistedPackagesAsync queuing.
            //   2. PauseAll() so even a manually-queued file can't run: the post is buffered on
            //      the not-yet-started scheduler channel (FIFO) and drains as IsPaused=true the
            //      moment PackageManager's ctor calls Start().
            bool isAgent = desktop.Args?.Contains("--agent", StringComparer.Ordinal) == true;
            if (isAgent)
            {
                AppSettings settings = _serviceProvider.GetRequiredService<AppSettings>();
                settings.ForceAutostartUploadsNever();
                _serviceProvider.GetRequiredService<UploadScheduler>().PauseAll();
            }

            // Global UI-thread exception handler. AvaloniaUiDispatcher's marshaled path deliberately
            // lets an unsunk exception propagate into the dispatcher loop; without this wiring that
            // rethrow crashes the process (verified: Dispatcher.Post uses throwOnUiThread:true and
            // rethrows unless a handler marks the args Handled). Log + Handled = keep parity (WPF has
            // no such crash today), route instead of kill.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                _serviceProvider!.GetRequiredService<IAppLogger>().Log(this, LogType.Error,
                    $"Unhandled exception on the UI thread: {e.Exception}");
                e.Handled = true;
            };

#if DEBUG && WINDOWS
            // DEBUG-only dev flag, opened from the Opened hook below: the dev gallery (--gallery, non-modal).
            // Declared under #if DEBUG so it never OPENS anything in Release; the window type ships as dead
            // code (trigger-gated convention). Also gated on WINDOWS: the gallery demos the WebView2 login
            // window, which the portable build excludes.
            bool gallery = desktop.Args?.Contains("--gallery", StringComparer.Ordinal) == true;
#endif

            // Register the global Window.Loaded class handler so every window picks up the dark title bar
            // automatically (Win10 fallback; Win11 auto-recolors).
            Lib.UI.AvaloniaImmersiveDarkMode.RegisterGlobalHandler();

            // Restore the saved theme BEFORE the first window exists, so a dark-theme user never sees the
            // shell paint light and flip. MainViewModel.InitializeAsync restores the same setting, but it
            // runs from MainWindow.Opened — after the window is on screen, and behind a database init and
            // a log hydration — so it could never prevent the flash its own comment describes. That call
            // stays: it owns the IsDarkMode property the menu binds to, and re-applying the value it
            // already has is a no-op.
            //
            // Order matters twice over: this must also precede the window's construction, because
            // ApplyTheme sets the cached dark-chrome preference the global Loaded handler reads, and the
            // title bar is the one part that can't be re-styled without a repaint anyone would notice.
            if (StartupTheme.ReadPersistedDarkMode(
                    _serviceProvider.GetRequiredService<IDbContextFactory<Dal.CSUploaderDbContext>>()) is { } savedDark)
            {
                _serviceProvider.GetRequiredService<IThemeApplier>().ApplyTheme(savedDark);
            }

            Views.MainWindow mainWindow = new(
                _serviceProvider.GetRequiredService<AppSettings>(),
                _serviceProvider.GetRequiredService<ITrayIconService>(),
                _serviceProvider.GetRequiredService<Dal.SettingRepository>())
            {
                DataContext = _serviceProvider.GetRequiredService<MainViewModel>(),
            };

            // Emit the --agent confirmation only AFTER MainViewModel is resolved: its ctor is what
            // subscribes to IAppLogger.OnLogOutput (via LogsViewModel), so a Status line logged
            // before this point has no listener and is silently dropped. The latch + PauseAll above
            // must still run before MainViewModel (they gate the scheduler its PackageManager starts),
            // so only the log — which has no ordering constraint against the scheduler — moves here.
            if (isAgent)
            {
                _serviceProvider.GetRequiredService<IAppLogger>().Log(this, LogType.Status,
                    "--agent: AutostartUploads forced to Never; scheduler started paused.");
            }

            // Mirror the WPF head's MainWindow.Loaded trigger (MainWindow.xaml.cs:40-49): hydrate the
            // ViewModel (DB init, settings/proxies/packages/uploaded), then sync the tray icon to the
            // now-loaded AppSettings. Same awaited/fire-and-forget shape (async void event handler).
            //
            // ONE-SHOT: WPF's Loaded fires once, but Avalonia re-raises Opened on EVERY Hide()->Show()
            // (Phase 7 close-to-tray makes hide->show reachable). The HYDRATION is not idempotent —
            // re-running it would duplicate packages, re-persist logs N+1, risk re-scheduling and open a
            // second --gallery window — but InitializeAsync now caches its task, so calling it again is
            // safe. This guard remains for the POST-init work below, which has no such cache. It also
            // deliberately skips the post-init UpdateVisibility / --gallery re-runs on a
            // tray restore (WPF never re-ran those either).
            bool hydrated = false;
            mainWindow.Opened += async (_, _) =>
            {
                if (hydrated)
                {
                    return;
                }

                hydrated = true;

                try
                {
                    if (mainWindow.DataContext is MainViewModel viewModel)
                    {
                        // Recorded on BOTH paths. The gated one assigns this before awaiting; the
                        // ungated one has to do it here, or --agent, --gallery, loose builds and
                        // preference-disabled runs reach Exit with it still null and dispose the
                        // provider under an in-flight EF call - the exact case the guard exists for.
                        _startupPipeline ??= viewModel.InitializeAsync();
                        await _startupPipeline;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The app is closing - the splash was dismissed, or the main window went away
                    // while the remainder was still loading. That is a shutdown, not a failure, and
                    // reporting it would put an error dialog in front of someone who just quit.
                    return;
                }
                catch (Exception ex)
                {
                    // Startup hydration (DB init, settings/proxies/packages load) failed. Surface it
                    // instead of leaving a half-initialized window silently up: log it, mark the title
                    // so the failure is visible, then show a modal error dialog. Skip the post-init
                    // steps below (tray sync, gallery) — they assume a hydrated ViewModel. MainWindow is
                    // shown at this point (we are in its Opened handler), so the owner resolver finds it
                    // and the box is modal over it; the exception text is not localizable, so the title
                    // falls back to Common_Error (no new i18n key).
                    _serviceProvider.GetRequiredService<IAppLogger>().Log(this, LogType.Error,
                        $"Startup initialization failed: {ex}");
                    mainWindow.Title = "CSUploader — startup failed (see logs)";

                    // The error dialog itself can throw (owner/StorageProvider resolution, a disposed
                    // provider), and this is an async-void handler — an escaping exception would reach
                    // the dispatcher loop and, past the UnhandledException hook's Handled marking, still
                    // risks tearing down startup. Best-effort: a secondary failure is logged only, so the
                    // primary startup error stays the reported cause.
                    try
                    {
                        await _serviceProvider.GetRequiredService<IDialogService>()
                            .ShowErrorAsync($"Startup initialization failed:\n\n{ex.Message}");
                    }
                    catch (Exception dialogEx)
                    {
                        _serviceProvider.GetRequiredService<IAppLogger>().Log(this, LogType.Error,
                            $"Failed to show the startup-failure error dialog: {dialogEx}");
                    }

                    return;
                }

                _serviceProvider.GetRequiredService<ITrayIconService>().UpdateVisibility();

#if DEBUG && WINDOWS
                if (gallery)
                {
                    // Dev gallery: non-modal Show() so it coexists with the shell (the bridge drives
                    // its named buttons for the phase contact sheet). Ctor takes the REAL IThemeApplier,
                    // IDialogService and IUpdateProgressSink so its theme/grid-font toggles, dialog
                    // launchers and the update-progress toggle hit the exact production paths.
                    new DevTools.GalleryWindow(
                        _serviceProvider.GetRequiredService<IThemeApplier>(),
                        _serviceProvider.GetRequiredService<IDialogService>(),
                        _serviceProvider.GetRequiredService<IUpdateProgressSink>()).Show();
                }
#endif
            };

            // === startup gate =================================================================
            // Whether to hold the main window back behind an update check. Ordered so the excluded
            // modes cost nothing: the dev flags short-circuit before the updater is consulted and
            // before the database is touched.
            //
            // IsInstalled is what excludes loose builds and `dotnet run` - they have no Velopack
            // layout, so a check there would be an instant no-op behind a splash that flashed for a
            // frame. --agent and --gallery are separate: an INSTALLED build launched with them is
            // still installed, and the bridge/gallery flows must not grow a window they never had.
            bool gateStartup =
                !isAgent
#if DEBUG && WINDOWS
                && !gallery
#endif
                && _serviceProvider.GetRequiredService<Lib.Update.IUpdateService>().IsInstalled
                && StartupUpdatePreference.ReadAskToUpdateAtStartup(
                    _serviceProvider.GetRequiredService<IDbContextFactory<Dal.CSUploaderDbContext>>()) != false;

            if (gateStartup)
            {
                StartSplashGatedStartup(desktop, mainWindow);
            }
            else
            {
                desktop.MainWindow = mainWindow;
            }

#if AVA_BRIDGE
            // Debug-only, opt-in (Directory.Build.local.props present) agent dev-loop bridge.
            // EnableMutations lets the ava_* tools drive controls; the redactor adds CSUploader's
            // cookie/userhash credential shapes on top of the bridge's built-in secret masking.
            // Attaching here subscribes to desktop.Startup, which fires after this method returns.
            this.AttachAgentBridge(o =>
            {
                // Read-only bridge inspection for casual Debug runs; mutations (ava_action driving
                // controls) require the guarded --agent mode, which also applied the latch + PauseAll
                // above — so nothing an agent clicks can kick off a real upload.
                o.EnableMutations = isAgent;
                o.Redactor = new Diagnostics.BridgeRedactor();
            });
#endif

            // Mirror App.OnExit: dispose the provider (and its IDisposable singletons — tray icon,
            // DbContext factory) when the app exits.
            desktop.Exit += (_, _) =>
            {
                // Skipped while the startup pipeline is still running. Disposing the provider under
                // an in-flight EF call throws ObjectDisposedException on a continuation nothing is
                // left to observe, and the process is going away anyway - the OS reclaims what this
                // would have released. Reached when the splash is closed before the swap, or the
                // main window is closed while the post-gate remainder is still loading.
                if (_startupPipeline is { IsCompleted: false })
                {
                    _serviceProvider?.GetService<IAppLogger>()?.Log(
                        this, LogType.Status, "Exiting while startup is still running; leaving the service provider to the process.");
                    return;
                }

                _serviceProvider?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>How long the splash may hold the main window back before startup carries on without
    /// the answer. Long enough for one HTTP round trip on a slow link; short enough not to read as a
    /// hang on a broken one.</summary>
    private static readonly TimeSpan StartupCheckDeadline = TimeSpan.FromSeconds(5);

    private Task? _startupPipeline;

    /// <summary>
    /// Shows the splash as the application's MainWindow, runs initialisation behind it, and swaps in
    /// the real window when initialisation says it may.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The splash IS the MainWindow until the swap, which is what keeps
    /// <see cref="ShutdownMode.OnMainWindowClose"/> working the whole way through: Avalonia decides
    /// shutdown by comparing the closing window against the CURRENT MainWindow, so closing the
    /// splash before the swap exits the app — which is what closing a splash should do — and closing
    /// it after the swap does nothing, because by then it is not the main window any more.
    /// </para>
    /// <para>
    /// It cannot be done inline in <c>OnFrameworkInitializationCompleted</c>: that method is
    /// synchronous, and Avalonia shows <c>MainWindow</c> and starts pumping only after it returns.
    /// So the sequence hangs off the splash's own <c>Opened</c>.
    /// </para>
    /// </remarks>
    private void StartSplashGatedStartup(IClassicDesktopStyleApplicationLifetime desktop, Views.MainWindow mainWindow)
    {
        Views.SplashWindow splash = new();
        CancellationTokenSource startupAborted = new();
        ViewModels.StartupGate gate = new(StartupCheckDeadline, startupAborted.Token);
        bool transitioning = false;

        if (mainWindow.DataContext is MainViewModel viewModel)
        {
            viewModel.StartupGate = gate;
        }

        // A user closing the splash before the swap is TERMINAL. It is still the main window, so
        // Avalonia is already shutting down; all this has to do is stop initialisation waiting for a
        // transition that will never come, and stop itself trying to make one.
        splash.Closing += (_, _) =>
        {
            if (transitioning)
            {
                return;
            }

            startupAborted.Cancel();
            gate.Abandon();
        };

        splash.Opened += async (_, _) =>
        {
            try
            {
                _startupPipeline = viewModelOrNull(mainWindow)?.InitializeAsync() ?? Task.CompletedTask;

                if (!await gate.WaitToShowMainWindowAsync(_startupPipeline))
                {
                    // Abandoned while waiting - the splash was closed after the wait completed but
                    // before this continuation ran. Showing the window now would put one on screen
                    // for an app that has already decided to quit.
                    return;
                }

                transitioning = true;
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();
                gate.MarkMainWindowReady();
            }
            catch (OperationCanceledException)
            {
                // The splash was closed. Nothing to show, nothing to report.
            }
            catch (Exception ex)
            {
                // The transition itself failed - Show() threw, say. Leaving the lifetime pointing at
                // an invisible window would be a process with no UI and no way to quit, so this
                // gives up loudly rather than quietly.
                //
                // Recovery FIRST, logging second: the logger raises its event inline, so a throwing
                // subscriber would otherwise take the shutdown with it.
                startupAborted.Cancel();
                gate.Abandon();
                try
                {
                    _serviceProvider?.GetService<IAppLogger>()?.Log(
                        this, LogType.Error, $"Startup transition failed: {ex}");
                }
                catch (Exception)
                {
                    // Nothing left to report it to.
                }

                desktop.Shutdown(1);
            }
        };

        // Closed, not Closing: Closing is cancellable and fires for close-to-tray, which must NOT
        // cancel initialisation. By the time Closed runs the window is really going away, and the
        // post-gate remainder - proxies, packages, history - has nothing left to load them for.
        mainWindow.Closed += (_, _) =>
        {
            startupAborted.Cancel();
            gate.Abandon();
        };

        desktop.MainWindow = splash;

        static MainViewModel? viewModelOrNull(Views.MainWindow window) => window.DataContext as MainViewModel;
    }

    // internal so the Avalonia head's DI smoke test can build the same provider this composes at
    // startup, mirroring App.xaml.cs:58-91. Everything UI-agnostic lives in Core (AddCoreServices);
    // this head supplies only the Avalonia implementations of the UI interfaces below.
    internal static void ConfigureServices(IServiceCollection services, string baseDirectory)
    {
        services.AddCoreServices(baseDirectory);

        // UI services (Avalonia implementations of Core interfaces)
        services.AddSingleton<IDialogService, AvaloniaDialogService>();            // real message box + startup error + StorageProvider pickers; ported dialog windows land through later Phase 4 tasks; 3 account/proxy members Phase 5
        services.AddSingleton<IUpdateProgressSink, AvaloniaUpdateProgressSink>();   // real: non-modal UpdateProgressWindow (Phase 4 Task 8)
        services.AddSingleton<IStartupUpdatePrompt, AvaloniaStartupUpdatePrompt>();  // real: the modal UpdatePromptWindow, shown once at startup
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFontEnumerationService, AvaloniaFontEnumerationService>();
        services.AddSingleton<IThemeApplier, AvaloniaThemeApplier>();              // real: RequestedThemeVariant + grid-font resources
#if WINDOWS
        services.AddSingleton<IInteractiveAuthService, AvaloniaWebViewInteractiveAuthService>(); // real WebView2 sign-in (Phase 8)
#else
        // Portable (Linux/macOS) build: WebView2 is Windows-only, so the captcha sign-in runs on CefGlue/CEF
        // (behind the same seam). UnsupportedInteractiveAuthService is retained as the last-resort fallback for
        // any non-Windows platform CEF cannot serve (e.g. musl/Alpine — glibc-only CEF), but is not registered.
        services.AddSingleton<IInteractiveAuthService, CefGlueInteractiveAuthService>(); // real CefGlue sign-in (Linux/macOS)
#endif
        services.AddSingleton<AvaloniaTrayIconService>();
        // Same singleton instance is reachable through the Core interface too, so the shared
        // ViewModels depend on ITrayIconService, not the Avalonia tray type.
        services.AddSingleton<ITrayIconService>(sp => sp.GetRequiredService<AvaloniaTrayIconService>());
        services.AddSingleton<IToastWindowFactory, AvaloniaToastWindowFactory>();
        services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IToastWindowFactory>(),
            workAreaProvider: () =>
            {
                // Primary-screen work area in DIPs (design: ALL toast geometry is in DIPs). MainWindow is shown
                // by the time any toast fires, so its Screens.Primary is valid; fall back on the rare null.
                Window? main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                var screen = main?.Screens?.Primary; // Screen (element type) is Avalonia.Platform; var avoids importing it
                return screen is null
                    ? new DipRect(0, 0, 1920, 1080)
                    : Lib.UI.ToastPlacement.WorkAreaToDip(screen.WorkingArea, screen.Scaling);
            },
            activate: () => sp.GetRequiredService<MainViewModel>().ActivateAndShowUploadedTab(),
            dispatcher: sp.GetRequiredService<IUiDispatcher>())); // real bottom-right completion toasts (Phase 7)

        // ViewModels are registered by AddCoreServices — framework-free and shared with the WPF head.
    }
}
