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
    public void ShowFileCompleted(PackageFile file);

    /// <summary>
    /// Raises a per-package summary toast.
    /// </summary>
    /// <param name="package">The package whose run finished.</param>
    /// <param name="succeeded">Files that reached <see cref="FileState.Completed"/>.</param>
    /// <param name="total">Total files in the package.</param>
    public void ShowPackageCompleted(Package package, int succeeded, int total);
}
