// <copyright file="UploadsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class UploadsView : UserControl
{
    private ContextMenu? _headerContextMenu;

    public UploadsView()
    {
        InitializeComponent();
    }

    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.DataContext is Package package && DataContext is UploadsViewModel vm)
        {
            package.IsExpanded = btn.IsChecked == true;
        }
    }

    private void UploadsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || _headerContextMenu is not null)
        {
            return;
        }

        _headerContextMenu = BuildColumnToggleMenu(grid);

        // Apply the menu to every column header via style (shared across headers).
        var headerStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
        headerStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, _headerContextMenu));
        grid.ColumnHeaderStyle = headerStyle;
    }

    private static ContextMenu BuildColumnToggleMenu(DataGrid grid)
    {
        var menu = new ContextMenu();

        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? "Column";
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };

            // Name column is the anchor for the expand toggle — don't allow hiding.
            if (grid.Columns.IndexOf(column) == 0)
            {
                item.IsEnabled = false;
            }
            else
            {
                DataGridColumn capturedColumn = column;
                MenuItem capturedItem = item;
                item.Click += (_, _) =>
                {
                    capturedColumn.Visibility = capturedItem.IsChecked
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                };
            }

            menu.Items.Add(item);
        }

        // Refresh checkmarks each time the menu opens in case visibility changed elsewhere.
        menu.Opened += (_, _) =>
        {
            for (int i = 0; i < menu.Items.Count && i < grid.Columns.Count; i++)
            {
                if (menu.Items[i] is MenuItem mi)
                {
                    mi.IsChecked = grid.Columns[i].Visibility == Visibility.Visible;
                }
            }
        };

        return menu;
    }

    private void AddUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UploadsViewModel vm)
        {
            var wizard = new UploadWizardWindow(vm)
            {
                Owner = Window.GetWindow(this),
            };
            wizard.ShowDialog();
        }
    }

    private void OverviewCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UploadsViewModel vm)
        {
            vm.ShowUploadOverview = false;
        }
    }

    /// <summary>
    /// Toggles the column-width lock for the column whose header was clicked.
    /// Walks up the visual tree from the ToggleButton to find the owning column.
    /// </summary>
    private void ColumnLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
        {
            return;
        }

        DependencyObject? cursor = toggle;
        while (cursor is not null and not DataGridColumnHeader)
        {
            cursor = VisualTreeHelper.GetParent(cursor);
        }

        if (cursor is DataGridColumnHeader header && header.Column is { } column)
        {
            column.CanUserResize = toggle.IsChecked != true;
        }
    }

    /// <summary>
    /// Switches to the Settings tab and selects the Accounts category.
    /// </summary>
    private void PremiumAccountLink_Click(object sender, MouseButtonEventArgs e)
    {
        Window? window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel main)
        {
            main.SelectedTabIndex = 2; // Settings tab
            main.SettingsViewModel.SelectedCategoryIndex = 3; // Accounts category (after Connection at 2)
        }
    }
}
