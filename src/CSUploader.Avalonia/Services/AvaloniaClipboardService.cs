using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IClipboardService"/>. Avalonia's clipboard hangs off a
/// <see cref="TopLevel"/>, resolved at call time through the shared <see cref="DialogOwnerResolver"/>. It
/// deliberately uses the resolver's OWN clipboard entry point — <see cref="DialogOwnerResolver.ResolveTopLevelForClipboard()"/>
/// (active-visible window ?? main window regardless of visibility) — not the modal-dialog owner chain: a
/// clipboard's platform impl is live on a tray-hidden window, so a Copy issued while the main window is
/// hidden must still reach its clipboard rather than silently no-op (visibility gates owner PARENTING, not
/// clipboard access). Only a genuinely absent window or a non-desktop/headless lifetime yields <c>null</c>,
/// and there the operations complete as no-ops.
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
        DialogOwnerResolver.ResolveTopLevelForClipboard()?.Clipboard;
}
