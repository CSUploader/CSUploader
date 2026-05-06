// <copyright file="SettingsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using CSUploader.Dal;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pops the Remove dropdown anchored under the button. Lets the single button slot
    /// host both "remove selected" and "remove failed" without crowding the toolbar.
    /// </summary>
    private void RemoveProxiesButton_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(sender);

    private void ImportProxiesButton_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(sender);

    private void ExportProxiesButton_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(sender);

    private static void OpenButtonContextMenu(object sender)
    {
        if (sender is Button button && button.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void AccountEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox
            && checkBox.DataContext is FileHosterLoginDto account
            && DataContext is SettingsViewModel vm)
        {
            // Disabled is the inverse of checked
            account.Disabled = checkBox.IsChecked != true;
            vm.SelectedAccount = account;
            vm.ToggleAccountCommand.Execute(account.Disabled ? "Disable" : "Enable");
        }
    }
}
