// <copyright file="SettingsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>
    /// Toggles which items appear in the proxy grid's context menu based on whether
    /// the right-click landed on a row or on empty grid space. On empty space the
    /// row-targeted Test/Remove items would have no meaningful selection to operate
    /// on, so we collapse them and leave only Add/Import/Export — the same actions
    /// available from the bottom button bar.
    /// </summary>
    private void ProxyGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool onRow = HitTestIsRow(e.OriginalSource as DependencyObject);
        Visibility rowVis = onRow ? Visibility.Visible : Visibility.Collapsed;
        ProxyContextTestItem.Visibility = rowVis;
        ProxyContextRemoveItem.Visibility = rowVis;
        ProxyContextRowSeparator.Visibility = rowVis;
        ProxyContextExportSelectedSeparator.Visibility = rowVis;
        ProxyContextExportSelectedToTextItem.Visibility = rowVis;
        ProxyContextExportSelectedToFileItem.Visibility = rowVis;
    }

    private static bool HitTestIsRow(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source is DataGridRow;
    }

    /// <summary>
    /// Mirrors <see cref="ProxyGrid_ContextMenuOpening"/>: collapses the row-targeted
    /// items (Edit / Refresh / Enable / Disable / Delete) when the right-click landed
    /// on empty grid space, leaving only the Add entry.
    /// </summary>
    private void AccountsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool onRow = HitTestIsRow(e.OriginalSource as DependencyObject);
        Visibility rowVis = onRow ? Visibility.Visible : Visibility.Collapsed;
        AccountsContextEditItem.Visibility = rowVis;
        AccountsContextRefreshItem.Visibility = rowVis;
        AccountsContextRowSeparator1.Visibility = rowVis;
        AccountsContextEnableItem.Visibility = rowVis;
        AccountsContextDisableItem.Visibility = rowVis;
        AccountsContextRowSeparator2.Visibility = rowVis;
        AccountsContextDeleteItem.Visibility = rowVis;
        AccountsContextRowSeparator3.Visibility = rowVis;
    }

    /// <summary>
    /// Opens the Edit Account dialog when the user double-clicks a row in the
    /// accounts grid. Walks up the visual tree from the click target so double-
    /// clicks on column headers / scroll bars / empty space are ignored.
    /// </summary>
    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow && DataContext is SettingsViewModel vm && vm.EditAccountCommand.CanExecute(null))
        {
            vm.EditAccountCommand.Execute(null);
            e.Handled = true;
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
