using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace CSUploader.Services;

/// <summary>
/// Resolves the <see cref="Window"/> a dialog (or a clipboard <c>TopLevel</c>) should attach to. Avalonia's
/// <c>ShowDialog</c> requires a non-null, visible owner and throws otherwise; this app hides its main
/// window to the tray, so — unlike the WPF head, whose <c>DialogService.ActiveOwner</c> could return null
/// harmlessly — the target has to be resolved deliberately. The head has ONE owner resolver with three
/// intentionally-distinct entry points, each the design's contract made operational for its surface:
/// <list type="bullet">
/// <item><see cref="Resolve"/> — a MODAL dialog's owner: active-visible window ?? visible main window ??
/// null. The caller decides on null: a message box shows ownerless, a picker reveals the tray-hidden main
/// window first (see <c>AvaloniaDialogService</c>).</item>
/// <item><see cref="ResolveVisibleMainOnly"/> — the owner for a surface that must OUTLIVE transient active
/// windows: the visible main window only, never a transient active window. Two consumers — the long-lived,
/// non-modal update-progress window, and the (modal) WebView login window, which must not be parented to
/// another transient login lest an owner-close cascade force-close it mid-captcha. The load-bearing property
/// is owner stability, not modality.</item>
/// <item><see cref="ResolveTopLevelForClipboard"/> — a clipboard's <c>TopLevel</c>: active-visible window
/// ?? main window regardless of visibility.</item>
/// </list>
/// <see cref="Window.IsVisible"/> is deliberately the exact property Avalonia's own owner guard checks
/// (not <c>IsEffectivelyVisible</c>). Static and stateless.
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

    /// <summary>
    /// The pure owner policy for a surface that must OUTLIVE transient active windows: the visible main
    /// window only. It deliberately ignores the active-window list (hence no <c>windows</c> parameter) — an
    /// owned window dies with its owner, so a surface that must survive a transient active window must never
    /// be parented to that transient. Two consumers: the update-progress window (long-lived, non-modal —
    /// survives the dialog that launched it, per the failure-window-stays-up contract) and the modal WebView
    /// login window (parenting it to another transient login would let an owner-close cascade force-close it
    /// mid-captcha). WPF parity: Owner = MainWindow.
    /// </summary>
    internal static Window? ResolveVisibleMainOnly(Window? mainWindow) =>
        mainWindow is { IsVisible: true } ? mainWindow : null;

    /// <summary>
    /// Reads the live desktop lifetime and applies <see cref="ResolveVisibleMainOnly(Window?)"/>. Returns
    /// <c>null</c> under a non-desktop lifetime (headless) or a tray-hidden main window — the caller then
    /// shows ownerless.
    /// </summary>
    internal static Window? ResolveVisibleMainOnly()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return ResolveVisibleMainOnly(desktop.MainWindow);
    }

    /// <summary>
    /// The pure policy for resolving the <c>TopLevel</c> that backs the clipboard: active-visible window
    /// ?? the main window REGARDLESS of visibility. This diverges from <see cref="Resolve"/> on the
    /// fallback's visibility guard on purpose — visibility is an owner-parenting concern (Avalonia rejects
    /// a hidden owner for Show/ShowDialog), but a clipboard hangs off a <c>TopLevel</c>'s platform impl,
    /// which is live whether or not the window is shown, so a tray-hidden Copy must still reach the main
    /// window's clipboard instead of silently no-opping. Pure (hand-built window list) for headless tests.
    /// </summary>
    internal static Window? ResolveTopLevelForClipboard(IEnumerable<Window> windows, Window? mainWindow)
    {
        Window? active = windows.FirstOrDefault(w => w is { IsActive: true, IsVisible: true });
        return active ?? mainWindow;
    }

    /// <summary>
    /// Reads the live desktop lifetime and applies <see cref="ResolveTopLevelForClipboard(IEnumerable{Window}, Window?)"/>.
    /// Returns <c>null</c> under a non-desktop lifetime (headless), where the clipboard operations no-op.
    /// </summary>
    internal static Window? ResolveTopLevelForClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return ResolveTopLevelForClipboard(desktop.Windows, desktop.MainWindow);
    }
}
