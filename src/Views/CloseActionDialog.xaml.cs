// <copyright file="CloseActionDialog.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Upload;

namespace CSUploader.Views;

/// <summary>
/// First-run prompt for the main window's X (close) button. Lets the user pick between
/// minimising to tray and exiting, with a "Remember my choice" checkbox so the next click
/// either repeats or re-prompts. Cancel keeps the window open and the setting unchanged.
/// </summary>
public partial class CloseActionDialog : Window
{
    public CloseActionDialog()
    {
        InitializeComponent();
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    /// <summary>
    /// What the user picked. Only meaningful when <see cref="DialogResult"/> is true.
    /// </summary>
    public CloseAction ChosenAction { get; private set; }

    /// <summary>
    /// True if the user ticked "Remember my choice" before confirming. When false the
    /// caller should keep <see cref="AppSettings.CloseAction"/> at <see cref="CloseAction.Ask"/>.
    /// </summary>
    public bool Remember => RememberCheck.IsChecked == true;

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        ChosenAction = CloseAction.MinimizeToTray;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ChosenAction = CloseAction.Exit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
