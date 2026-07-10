// <copyright file="IUpdateProgressSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Non-modal update-download progress surface. WPF: <c>UpdateProgressWindow</c>;
/// the Avalonia head supplies its own. Open/SetStatus/Report/Close are UI-thread-safe.
/// </summary>
/// <remarks>
/// Mirrors the WPF update flow exactly: <see cref="Open"/> shows the window,
/// <see cref="SetStatus"/> swaps the status line (downloading / restarting / failed),
/// and <see cref="Report"/> pumps the progress bar. The WPF flow never programmatically
/// <see cref="Close"/>s the window — on success the process restarts, on failure the window
/// stays open showing the error — so <see cref="Close"/> exists for the Avalonia head but is
/// unused by the current WPF caller.
/// </remarks>
public interface IUpdateProgressSink
{
    void Open();

    void SetStatus(string status);

    void Report(int percent);

    void Close();
}
