using Avalonia;
using Velopack;

namespace CSUploader;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack first-frame hook: handles --veloapp-install / --veloapp-uninstall
        // command-line flags that the installer fires. Must run before anything else.
        VelopackApp.Build().Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
#if !WINDOWS
        // Non-Windows interactive sign-in uses CefGlue/CEF; initialize CEF inside AfterSetup (after platform
        // detect, before the desktop lifetime + any AvaloniaCefBrowser). Windows uses WebView2 (no CEF).
        builder = builder.AfterSetup(_ => Services.CefBootstrap.Initialize());
#endif
        return builder;
    }
}
