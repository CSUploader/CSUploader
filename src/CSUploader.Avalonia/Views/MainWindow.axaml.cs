// <copyright file="MainWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class MainWindow : Window
{
    // Set when the user (menu Exit) or the close-to-tray Exit choice really wants to quit, bypassing
    // the close-to-tray rerouting in OnClosing. Mirrors the WPF MainWindow._forceClose.
#pragma warning disable CS0414 // Staged in Task 5 (Exit sets it); the Closing reroute that READS it lands in Task 6.
    private bool _forceClose;
#pragma warning restore CS0414

    public MainWindow()
    {
        InitializeComponent();
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.CheckForUpdatesAsync();
            string message = vm.IsUpdateAvailable
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Instance["Main_CheckForUpdates_Available_Format"], vm.AvailableVersion)
                : Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"];
            await MessageBoxWindow.ShowErrorAsync(this, message, Localizer.Instance["Main_CheckForUpdates_DialogTitle"]);
        }
    }

    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
