using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IClipboardService"/>. Avalonia's clipboard hangs off a
/// <see cref="TopLevel"/>, so — mirroring the <see cref="IDialogService"/> owner contract — the
/// clipboard is resolved from the currently-active window at call time (falling back to the main
/// window). When no window is available the operations complete as no-ops.
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        IClipboard? clipboard = ResolveClipboard();
        return clipboard is null ? Task.CompletedTask : clipboard.SetTextAsync(text);
    }

    public Task ClearAsync()
    {
        IClipboard? clipboard = ResolveClipboard();
        return clipboard is null ? Task.CompletedTask : clipboard.ClearAsync();
    }

    private static IClipboard? ResolveClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        Window? window = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        return window?.Clipboard;
    }
}
