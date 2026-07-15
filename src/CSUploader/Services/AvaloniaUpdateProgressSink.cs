// <copyright file="AvaloniaUpdateProgressSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IUpdateProgressSink"/>. Owns the non-modal
/// <see cref="UpdateProgressWindow"/> and mirrors <c>WpfUpdateProgressSink</c>: <see cref="Open"/> shows a
/// fresh window; <see cref="SetStatus"/>/<see cref="Report"/> drive it. All members run on the UI thread
/// (interface contract).
/// </summary>
public sealed class AvaloniaUpdateProgressSink : IUpdateProgressSink
{
    private UpdateProgressWindow? _window;

    /// <summary>
    /// Creates and shows a FRESH window, replacing any prior surface (the interface's retry contract: a
    /// second install attempt gets a clean window, and <see cref="Report"/>/<see cref="SetStatus"/> then
    /// target this most-recent one). A previous window is NOT closed — WPF parity: on failure the old
    /// window stays up showing its error, and the caller drives only the latest.
    /// </summary>
    public void Open()
    {
        UpdateProgressWindow window = new();
        _window = window;

        // Owner = the visible MAIN window only, else ownerless — NOT the active-visible resolver a modal
        // dialog uses. This is a long-lived, non-modal surface that must outlive whatever launched the
        // update: an owned window dies with its owner, so parenting it to a transient active window would
        // break the failure-window-stays-up contract (on a failed install the window stays up showing the
        // error). WPF parity: Owner = MainWindow. Avalonia's Show(owner) throws on a hidden owner exactly
        // like ShowDialog (§Reality-check #12, verified in UpdateProgressSinkTests), and the resolver only
        // ever returns a visible window, so passing it is safe. The null branch (main hidden to the tray,
        // or the headless test lifetime) shows ownerless with a taskbar entry so a tray-hidden user can
        // re-find it — never yanking the tray-hidden main window up for progress it did not ask to see.
        Window? owner = DialogOwnerResolver.ResolveVisibleMainOnly();
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.ShowInTaskbar = true;
            window.Show();
        }
    }

    public void SetStatus(string status) => _window?.SetStatus(status);

    public void Report(int percent) => _window?.SetProgress(percent);

    /// <summary>
    /// Closes the current window and drops the reference. Unused by the current WPF caller (on success the
    /// process restarts; on failure the window stays up showing the error — see the interface remarks);
    /// present for interface completeness and the gallery toggle driver.
    /// </summary>
    public void Close()
    {
        _window?.Close();
        _window = null;
    }

    // Test seam (InternalsVisibleTo → CSUploader.Tests): the current (most-recent) window, so the
    // headless sink tests can assert fresh-window-per-Open, Report-updates-the-bar, and the never-Close
    // lifecycle contracts against the real controls.
    internal UpdateProgressWindow? CurrentWindow => _window;
}
