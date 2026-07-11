using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IClipboardService"/>. Avalonia's clipboard hangs off a
/// <see cref="TopLevel"/>, so — mirroring the <see cref="IDialogService"/> owner contract — the
/// clipboard is resolved through the shared <see cref="DialogOwnerResolver"/> (active-visible window
/// ?? visible main window) at call time. When the resolver yields <c>null</c> (no ownable window, or a
/// non-desktop/headless lifetime) the operations complete as no-ops — the null-tolerant behavior the
/// clipboard has always had, now sharing the one owner-resolution policy the design mandates.
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

    private static IClipboard? ResolveClipboard() =>
        DialogOwnerResolver.ResolveFromLifetime()?.Clipboard;
}
