// <copyright file="IUpdateProgressSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;

namespace CSUploader.Services;

/// <summary>
/// Non-modal update-download progress surface. WPF: <c>UpdateProgressWindow</c>;
/// the Avalonia head supplies its own. All members must be called on the UI thread (see remarks).
/// </summary>
/// <remarks>
/// Contract: callers must be on the UI thread. <see cref="Open"/> replaces any prior surface and
/// is called at most once per install attempt; <see cref="Report"/> and <see cref="SetStatus"/>
/// target the most recently opened surface. Mirrors the WPF update flow: <see cref="Open"/> shows
/// the window, <see cref="SetStatus"/> swaps the status line (downloading / restarting / failed),
/// and <see cref="Report"/> pumps the progress bar. The WPF flow never programmatically
/// <see cref="Close"/>s the window — on success the process restarts, on failure the window
/// stays open showing the error — so <see cref="Close"/> exists for the Avalonia head but is
/// unused by the current WPF caller.
/// </remarks>
public interface IUpdateProgressSink
{
    void Open();

    void SetStatus(string status);

    /// <summary>
    /// Pumps the bar and the figures beside it. Carries more than a percentage because the surface
    /// shows more than a bar — see <see cref="UpdateDownloadProgress"/> for what is measured and
    /// what is merely derived.
    /// </summary>
    void Report(UpdateDownloadProgress progress);

    void Close();
}
