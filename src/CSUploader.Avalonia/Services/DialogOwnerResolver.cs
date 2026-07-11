using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace CSUploader.Services;

/// <summary>
/// Resolves the <see cref="Window"/> that should own a dialog. Avalonia's <c>ShowDialog</c> requires
/// a non-null, visible owner and throws otherwise; this app hides its main window to the tray, so —
/// unlike the WPF head, whose <c>DialogService.ActiveOwner</c> could return null harmlessly — the
/// owner has to be resolved deliberately. The policy is the design's owner contract made operational:
/// <list type="number">
/// <item>the first active, visible window (a dialog opened from the modal upload wizard parents to the
/// wizard, per the Core owner contract);</item>
/// <item>else the main window, if it is visible;</item>
/// <item>else <c>null</c> — and the CALLER decides: a message box shows ownerless, while a modal dialog
/// or picker reveals the tray-hidden main window first (see <c>AvaloniaDialogService</c>).</item>
/// </list>
/// <see cref="Window.IsVisible"/> is deliberately the exact property Avalonia's own owner guard checks
/// (not <c>IsEffectivelyVisible</c>). Static and stateless, mirroring
/// <see cref="AvaloniaClipboardService"/>'s clipboard resolution.
/// </summary>
internal static class DialogOwnerResolver
{
    /// <summary>
    /// The pure policy chain: active-visible window ?? visible main window ?? null. Kept separate from
    /// the lifetime read so it is testable headlessly with a hand-built window list (the headless
    /// session is not a desktop lifetime).
    /// </summary>
    internal static Window? Resolve(IEnumerable<Window> windows, Window? mainWindow)
    {
        Window? active = windows.FirstOrDefault(w => w is { IsActive: true, IsVisible: true });
        if (active is not null)
        {
            return active;
        }

        return mainWindow is { IsVisible: true } ? mainWindow : null;
    }

    /// <summary>
    /// Reads the live desktop lifetime and applies <see cref="Resolve"/>. Returns <c>null</c> under a
    /// non-desktop lifetime (e.g. the headless test session), which is why the policy chain is factored
    /// into the pure <see cref="Resolve"/> overload above.
    /// </summary>
    internal static Window? ResolveFromLifetime()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return Resolve(desktop.Windows, desktop.MainWindow);
    }
}
