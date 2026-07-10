namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IUpdateProgressSink"/>. No-op in Phase 2 — the
/// UpdateProgress window arrives in Phase 4. All four members stay inert so the shared update flow
/// can drive them without throwing. (The WPF install flow never calls <see cref="Close"/>; see the
/// interface remarks.)
/// </summary>
public sealed class AvaloniaUpdateProgressSink : IUpdateProgressSink
{
    public void Open()
    {
        // TODO(phase4): show the non-modal update-progress window.
    }

    public void SetStatus(string status)
    {
        // TODO(phase4): swap the status line (downloading / restarting / failed).
    }

    public void Report(int percent)
    {
        // TODO(phase4): pump the progress bar.
    }

    public void Close()
    {
        // TODO(phase4): close the progress window (unused by the current WPF caller).
    }
}
