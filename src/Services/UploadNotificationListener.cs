// <copyright file="UploadNotificationListener.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Translates <see cref="UploadScheduler.FileStateChanged"/> events into toast service
/// calls. Tracks per-package "summary already fired" so retries after a failure don't
/// re-trigger the package-completion summary.
/// </summary>
public sealed class UploadNotificationListener : IDisposable
{
    private readonly UploadScheduler _scheduler;
    private readonly IToastNotificationService _toasts;
    private readonly ConditionalWeakTable<Package, object> _summaryShown = new();
    private static readonly object SummaryFiredSentinel = new();
    private volatile bool _disposed;

    public UploadNotificationListener(UploadScheduler scheduler, IToastNotificationService toasts)
    {
        _scheduler = scheduler;
        _toasts = toasts;
        _scheduler.FileStateChanged += HandleFileStateChanged;
    }

    /// <summary>
    /// Handles a <see cref="FileStateChangedEventArgs"/>. Internal so tests can drive it
    /// directly without the scheduler's full async pipeline.
    /// </summary>
    internal void HandleFileStateChanged(object? sender, FileStateChangedEventArgs e)
    {
        if (e.NewState == FileState.Completed)
        {
            _toasts.ShowFileCompleted(e.File);
        }

        if (!IsTerminal(e.NewState))
        {
            return;
        }

        Package pkg = e.File.Package;
        if (!AllFilesTerminal(pkg))
        {
            return;
        }

        int succeeded = 0;
        int total = 0;
        foreach (PackageFile f in pkg)
        {
            total++;
            if (f.State == FileState.Completed) succeeded++;
        }

        if (succeeded == 0)
        {
            return;
        }

        if (_summaryShown.TryAdd(pkg, SummaryFiredSentinel))
        {
            _toasts.ShowPackageCompleted(pkg, succeeded, total);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.FileStateChanged -= HandleFileStateChanged;
    }

    private static bool IsTerminal(FileState state) =>
        state is FileState.Completed or FileState.Failed or FileState.Cancelled;

    private static bool AllFilesTerminal(Package pkg)
    {
        foreach (PackageFile f in pkg)
        {
            if (!IsTerminal(f.State)) return false;
        }
        return true;
    }
}
