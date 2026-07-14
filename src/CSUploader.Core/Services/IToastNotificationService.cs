// <copyright file="IToastNotificationService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Surface for raising bottom-right completion-toast popups. Implementations check the
/// <see cref="AppSettings.ShowCompletionToasts"/> gate and silently no-op when it is false.
/// </summary>
public interface IToastNotificationService
{
    /// <summary>
    /// Raises a per-file "upload finished" toast.
    /// </summary>
    void ShowFileCompleted(PackageFile file);

    /// <summary>
    /// Raises a per-package summary toast.
    /// </summary>
    /// <param name="package">The package whose run finished.</param>
    /// <param name="succeeded">Files that reached <see cref="FileState.Completed"/>.</param>
    /// <param name="total">Total files in the package.</param>
    void ShowPackageCompleted(Package package, int succeeded, int total);

    /// <summary>
    /// Raises a general-purpose informational toast (title + body). Unlike the completion methods this is
    /// NOT gated on <see cref="AppSettings.ShowCompletionToasts"/> — it is a tray-discovery notice, not an
    /// upload completion. The Avalonia head routes the "still running in the tray" tip here (design section
    /// Tray balloon tip: Avalonia's TrayIcon has no balloon API); the WPF head keeps its native NotifyIcon
    /// balloon and does not call this.
    /// </summary>
    /// <param name="title">Toast title line.</param>
    /// <param name="body">Toast body line.</param>
    void ShowInfo(string title, string body);
}
