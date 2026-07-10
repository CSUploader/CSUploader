using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IToastNotificationService"/>. No-op in Phase 2 — real
/// bottom-right completion toasts arrive in Phase 7. This replaces the WPF head's
/// <c>IToastWindowFactory</c> + <c>ToastNotificationService</c> factory wiring for now, so
/// <c>UploadNotificationListener</c> can resolve and run without raising any UI.
/// </summary>
public sealed class NoOpToastNotificationService : IToastNotificationService
{
    public void ShowFileCompleted(PackageFile file)
    {
        // TODO(phase7): raise a per-file "upload finished" toast.
    }

    public void ShowPackageCompleted(Package package, int succeeded, int total)
    {
        // TODO(phase7): raise a per-package summary toast.
    }
}
