// <copyright file="LogsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CSUploader.Lib.Localization;
using CSUploader.Lib.UI;
using CSUploader.Services;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class LogsView : UserControl
{
    // XAML defaults per grid, captured at first Loaded before any persisted overrides are
    // applied, so "Reset columns" can restore the developer's shipped layout per grid.
    private readonly Dictionary<DataGrid, Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState>> _defaultColumnState = [];

    public LogsView()
    {
        InitializeComponent();
    }

    private async void LogGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire more than once (tab switches); only wire each grid up once.
        if (sender is not DataGrid grid || grid.Tag is not string settingKey || _defaultColumnState.ContainsKey(grid))
        {
            return;
        }

        _defaultColumnState[grid] = DataGridColumnVisibilityPersistence.CaptureCurrentState(grid);

        if (DataContext is LogsViewModel vm && vm.SettingRepo is { } repo)
        {
            await DataGridColumnVisibilityPersistence.ApplyAsync(grid, repo, settingKey);
        }

        ContextMenu menu = BuildColumnToggleMenu(grid, settingKey);
        var headerStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
        headerStyle.Setters.Add(new Setter(ContextMenuProperty, menu));
        grid.ColumnHeaderStyle = headerStyle;

        grid.ColumnDisplayIndexChanged += async (_, _) =>
        {
            if (DataContext is LogsViewModel innerVm && innerVm.SettingRepo is { } innerRepo)
            {
                await DataGridColumnVisibilityPersistence.PersistAsync(grid, innerRepo, settingKey);
            }
        };
    }

    private ContextMenu BuildColumnToggleMenu(DataGrid grid, string settingKey)
    {
        var menu = new ContextMenu();
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? Localizer.Instance["Uploads_ColumnMenu_DefaultLabel"];
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };

            // Keep the first column (DateTime) always visible — it's the anchor that guarantees
            // at least one header remains right-clickable to reopen this menu.
            if (grid.Columns.IndexOf(column) == 0)
            {
                item.IsEnabled = false;
            }
            else
            {
                DataGridColumn capturedColumn = column;
                MenuItem capturedItem = item;
                item.Click += async (_, _) =>
                {
                    capturedColumn.Visibility = capturedItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    if (DataContext is LogsViewModel vm && vm.SettingRepo is { } repo)
                    {
                        await DataGridColumnVisibilityPersistence.PersistAsync(grid, repo, settingKey);
                    }
                };
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        MenuItem resetItem = new() { Header = Localizer.Instance["Uploads_ColumnMenu_Reset"] };
        resetItem.Click += async (_, _) =>
        {
            if (!_defaultColumnState.TryGetValue(grid, out var defaults)
                || DataContext is not LogsViewModel vm
                || vm.SettingRepo is not { } repo)
            {
                return;
            }

            if (!await vm.DialogServiceForView.ShowOptOutConfirmationAsync(
                    ConfirmationKeys.ResetColumns,
                    Localizer.Instance["Logs_ResetColumns_Message"],
                    Localizer.Instance["Logs_ResetColumns_Title"]))
            {
                return;
            }

            await DataGridColumnVisibilityPersistence.ResetAsync(grid, defaults, repo, settingKey);
        };
        menu.Items.Add(resetItem);

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

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only open details when the double-click actually landed on a row — not on the column
        // header, the empty area below the rows, or a scrollbar. Otherwise a double-click
        // anywhere on the grid would re-open the previously-selected row's details. Use the
        // double-clicked row's own item rather than SelectedItem so it opens what was clicked.
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: LogEntryViewModel entry })
        {
            OpenDetails(entry);
        }
    }

    private void DataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter opens the selected row's details, mirroring a double-click. Handled during
        // tunnelling so the DataGrid's default Enter behaviour (move selection to the next row)
        // doesn't run instead.
        if (e.Key == Key.Enter && sender is DataGrid dg && dg.SelectedItem is LogEntryViewModel entry)
        {
            OpenDetails(entry);
            e.Handled = true;
        }
    }

    private void OpenDetails(LogEntryViewModel entry)
    {
        // HttpDetailsWindow for HTTP entries with transaction data; LogDetailsWindow otherwise.
        Window window = entry.HasHttpTransaction
            ? new HttpDetailsWindow(entry)
            : new LogDetailsWindow(entry);

        window.Owner = Window.GetWindow(this);
        window.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }
}
