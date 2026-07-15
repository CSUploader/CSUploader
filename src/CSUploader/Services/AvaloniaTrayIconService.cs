using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="ITrayIconService"/> over the built-in
/// <see cref="TrayIcon"/>. Mirrors the WPF head's <c>TrayIconManager</c>: visibility is driven by
/// <see cref="AppSettings.MinimizeToTray"/> and <see cref="AppSettings.CloseAction"/> (the icon
/// only appears when at least one routes the window into the tray); single-click and the "Show
/// CSUploader" menu item restore the window; "Exit" shuts the app down.
/// </summary>
public sealed class AvaloniaTrayIconService(AppSettings settings, IAppLogger logger, IToastNotificationService toasts)
    : IDisposable, ITrayIconService
{
    // Rename-proof: derive the avares authority from the running assembly's name rather than
    // hard-coding it, so the assembly rename (CSUploader.Avalonia → CSUploader) didn't silently
    // break the tray icon lookup. typeof(App) resolves to CSUploader.App in the head assembly.
    private static readonly Uri IconUri =
        new($"avares://{typeof(App).Assembly.GetName().Name}/Assets/icon.ico");

    private TrayIcon? _trayIcon;
    private bool _disposed;
    private bool _firstHideTipShown;

    /// <summary>
    /// Reads <see cref="AppSettings"/> and creates/destroys the tray icon to match. Call after
    /// startup load and after the Settings page saves changes.
    /// </summary>
    public void UpdateVisibility()
    {
        if (_disposed)
        {
            return;
        }

        bool needIcon = settings.MinimizeToTray
            || settings.CloseAction == CloseAction.MinimizeToTray;

        if (needIcon)
        {
            EnsureIcon();
        }
        else
        {
            DisposeIcon();
        }
    }

    /// <summary>
    /// Shows the one-shot "we're in the tray" notice the first time the window hides this session. Avalonia's
    /// <see cref="TrayIcon"/> has no balloon API, so this routes through the app's own toast system (design
    /// section Tray balloon tip) — consistent styling, same i18n keys. Mirrors the WPF
    /// <c>TrayIconManager.NotifyHidden</c> first-hide guard: the flag isn't persisted, so every fresh process
    /// gets one tip, then silence.
    /// </summary>
    public void NotifyHidden()
    {
        if (_disposed || _firstHideTipShown)
        {
            return;
        }

        _firstHideTipShown = true;
        toasts.ShowInfo(
            Localizer.Instance["Tray_Balloon_Title"],
            Localizer.Instance["Tray_Balloon_Body"]);
    }

    /// <summary>
    /// Restores the main window from minimized/hidden state and brings it to front. Safe to call
    /// from background threads — marshals onto the UI dispatcher. Mirrors <c>TrayIconManager</c>'s
    /// restore sequence exactly.
    /// </summary>
    public void ShowMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        void Restore()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Restore();
        }
        else
        {
            Dispatcher.UIThread.Post(Restore);
        }
    }

    // Test seam (InternalsVisibleTo -> CSUploader.Tests): icon presence, for the EnsureIconForSession
    // strand-fix test. Behaviourally identical to inlining the null-check at the call site.
    internal bool HasIcon => _trayIcon is not null;

    /// <inheritdoc />
    public void EnsureIconForSession()
    {
        if (_disposed)
        {
            return;
        }

        EnsureIcon();
    }

    private void EnsureIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        try
        {
            using Stream iconStream = AssetLoader.Open(IconUri);
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = Localizer.Instance["Tray_Tooltip"],
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => ShowMainWindow();

            NativeMenu menu = new();
            NativeMenuItem showItem = new(Localizer.Instance["Tray_Menu_Show"]);
            showItem.Click += (_, _) => ShowMainWindow();
            menu.Add(showItem);
            menu.Add(new NativeMenuItemSeparator());
            NativeMenuItem exitItem = new(Localizer.Instance["Tray_Menu_Exit"]);
            exitItem.Click += (_, _) => ExitApplication();
            menu.Add(exitItem);
            _trayIcon.Menu = menu;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to create tray icon: {ex.Message}");
            DisposeIcon();
        }
    }

    private static void ExitApplication()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void DisposeIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeIcon();
    }
}
