// <copyright file="TrayIconManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Upload;
using Application = System.Windows.Application;

namespace CSUploader.Services;

/// <summary>
/// Owns the system-tray <see cref="NotifyIcon"/> for the main window. Visibility is driven
/// by <see cref="AppSettings.MinimizeToTray"/> and <see cref="AppSettings.CloseAction"/> —
/// the icon only appears when at least one of those routes the window into the tray.
/// Single-click and the "Show CSUploader" menu item restore the window; "Exit" tears down
/// the application via <see cref="Application.Shutdown()"/>.
/// </summary>
public sealed class TrayIconManager(AppSettings settings, IAppLogger logger) : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private bool _disposed;
    private bool _firstHideTipShown;

    /// <summary>
    /// Reads <see cref="AppSettings"/> and creates/destroys the tray icon to match. Call
    /// after startup load and after the Settings page saves changes.
    /// </summary>
    public void UpdateVisibility()
    {
        if (_disposed)
        {
            return;
        }

        bool needIcon = settings.MinimizeToTray
            || settings.CloseAction == CloseAction.MinimizeToTray;

        if (needIcon)
        {
            EnsureIcon();
        }
        else
        {
            DisposeIcon();
        }
    }

    /// <summary>
    /// Shows a one-shot balloon-tip notification the first time the window is hidden in
    /// a session, so users who didn't realise the tray was the destination notice it. The
    /// flag isn't persisted — every fresh process gets one tip, then silence.
    /// </summary>
    public void NotifyHidden()
    {
        if (_disposed || _firstHideTipShown || _notifyIcon is null)
        {
            return;
        }

        _firstHideTipShown = true;
        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                Localizer.Instance["Tray_Balloon_Title"],
                Localizer.Instance["Tray_Balloon_Body"],
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to show tray balloon tip: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the main window from minimized/hidden state and brings it to front.
    /// Safe to call from background threads — marshals onto the dispatcher.
    /// </summary>
    public void ShowMainWindow()
    {
        Application app = Application.Current;
        if (app?.MainWindow is not Window window)
        {
            return;
        }

        void Restore()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        if (window.Dispatcher.CheckAccess())
        {
            Restore();
        }
        else
        {
            window.Dispatcher.BeginInvoke(Restore);
        }
    }

    private void EnsureIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = Localizer.Instance["Tray_Tooltip"],
                Visible = true,
            };

            _notifyIcon.MouseClick += OnIconClicked;
            _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

            ContextMenuStrip menu = new();
            ToolStripMenuItem showItem = new(Localizer.Instance["Tray_Menu_Show"]);
            showItem.Click += (_, _) => ShowMainWindow();
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exitItem = new(Localizer.Instance["Tray_Menu_Exit"]);
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to create tray icon: {ex.Message}");
            DisposeIcon();
        }
    }

    private void OnIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowMainWindow();
        }
    }

    private static void ExitApplication()
    {
        Application app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            app.Shutdown();
        }
        else
        {
            app.Dispatcher.BeginInvoke((Action)app.Shutdown);
        }
    }

    private static Icon LoadAppIcon()
    {
        // Stream the WPF resource icon out of the assembly so we don't need a file on disk.
        Uri uri = new("pack://application:,,,/Properties/Images/Logo/icon.ico", UriKind.Absolute);
        System.Windows.Resources.StreamResourceInfo info = Application.GetResourceStream(uri);
        using Stream stream = info.Stream;
        return new Icon(stream);
    }

    private void DisposeIcon()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeIcon();
    }
}
