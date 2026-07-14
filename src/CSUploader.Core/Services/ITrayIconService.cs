// <copyright file="ITrayIconService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Abstraction over the system-tray icon. The WPF head is <c>TrayIconManager</c> (a WinForms
/// <c>NotifyIcon</c>); the Avalonia head supplies its own. Exposes only the members the shared
/// ViewModels need — visibility refresh after a settings change, the first-hide balloon tip, and
/// restoring the main window — so no framework tray type leaks into Core.
/// </summary>
public interface ITrayIconService
{
    /// <summary>Reads the current settings and creates/destroys the tray icon to match.</summary>
    void UpdateVisibility();

    /// <summary>Shows the one-shot "we're in the tray" balloon tip the first time the window hides.</summary>
    void NotifyHidden();

    /// <summary>
    /// Forces the tray icon to exist for the remainder of this session, regardless of the persisted
    /// <see cref="AppSettings.MinimizeToTray"/>/<see cref="AppSettings.CloseAction"/> settings. Used by the
    /// close-action "Minimize" choice when the user did NOT tick "Remember": the window hides but
    /// <see cref="UpdateVisibility"/> would otherwise tear the icon down (settings still say don't-minimize),
    /// stranding the app hidden with no icon. Honours the one-off choice WITHOUT mutating in-memory settings.
    /// </summary>
    void EnsureIconForSession();

    /// <summary>Restores the main window from minimized/hidden state and brings it to front.</summary>
    void ShowMainWindow();
}
