using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;
#if AVA_BRIDGE
using AvaDevBridge;
#endif

namespace CSUploader;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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

            // THROWAWAY (Phase 2): the WebView2 GO/NO-GO spike. Debug + --webview-spike only; the
            // window opens modally from the Opened hook below (ShowDialog needs a SHOWN owner).
            bool webviewSpike = desktop.Args?.Contains("--webview-spike", StringComparer.Ordinal) == true;

            Views.MainWindow mainWindow = new()
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
            mainWindow.Opened += async (_, _) =>
            {
                try
                {
                    if (mainWindow.DataContext is MainViewModel viewModel)
                    {
                        await viewModel.InitializeAsync();
                    }
                }
                catch (Exception ex)
                {
                    // Startup hydration (DB init, settings/proxies/packages load) failed. Surface it
                    // instead of leaving a half-initialized window silently up: log it and mark the
                    // title so the failure is visible. Skip the post-init steps below (tray sync,
                    // spike) — they assume a hydrated ViewModel. Phase 4 upgrades this to a modal
                    // error dialog once IDialogService is real.
                    _serviceProvider.GetRequiredService<IAppLogger>().Log(this, LogType.Error,
                        $"Startup initialization failed: {ex}");
                    mainWindow.Title = "CSUploader — startup failed (see logs)";
                    return;
                }

                _serviceProvider.GetRequiredService<ITrayIconService>().UpdateVisibility();

#if DEBUG
                if (webviewSpike)
                {
                    // Modal-from-birth over the shown MainWindow (verify point c). ShowDialog awaits
                    // until the spike window closes; the guard flag keeps every non-spike launch clean.
                    await new Spike.WebView2SpikeWindow().ShowDialog(mainWindow);
                }
#endif
            };

            desktop.MainWindow = mainWindow;

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
            desktop.Exit += (_, _) => _serviceProvider?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // internal so the Avalonia head's DI smoke test can build the same provider this composes at
    // startup, mirroring App.xaml.cs:58-91. Everything UI-agnostic lives in Core (AddCoreServices);
    // this head supplies only the Avalonia implementations of the UI interfaces below.
    internal static void ConfigureServices(IServiceCollection services, string baseDirectory)
    {
        services.AddCoreServices(baseDirectory);

        // UI services (Avalonia implementations of Core interfaces)
        services.AddSingleton<IDialogService, AvaloniaDialogService>();            // throws per member until Phase 4
        services.AddSingleton<IUpdateProgressSink, AvaloniaUpdateProgressSink>();  // no-op until Phase 4
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFontEnumerationService, AvaloniaFontEnumerationService>();
        services.AddSingleton<IThemeApplier, AvaloniaThemeApplier>();              // no-op until Phase 3
        services.AddSingleton<IInteractiveAuthService, StubInteractiveAuthService>(); // throws until Phase 8
        services.AddSingleton<AvaloniaTrayIconService>();
        // Same singleton instance is reachable through the Core interface too, so the shared
        // ViewModels depend on ITrayIconService, not the Avalonia tray type.
        services.AddSingleton<ITrayIconService>(sp => sp.GetRequiredService<AvaloniaTrayIconService>());
        services.AddSingleton<IToastNotificationService, NoOpToastNotificationService>(); // real toasts in Phase 7

        // ViewModels are registered by AddCoreServices — framework-free and shared with the WPF head.
    }
}
