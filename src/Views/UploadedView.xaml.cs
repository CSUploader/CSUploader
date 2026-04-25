// <copyright file="UploadedView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CSUploader.Views;

public partial class UploadedView : UserControl
{
    private ContextMenu? _headerContextMenu;

    public UploadedView()
    {
        InitializeComponent();
    }

    private void FilesGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || _headerContextMenu is not null)
        {
            return;
        }

        _headerContextMenu = BuildColumnToggleMenu(grid);

        // Update the grid-level header style so future headers inherit the menu.
        Style gridHeaderStyle = new(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
        gridHeaderStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, _headerContextMenu));
        grid.ColumnHeaderStyle = gridHeaderStyle;

        // Per-column HeaderStyle (e.g. FirstHeaderStyle on Name) overrides the grid-level one,
        // so we also need to derive a new style from each column's existing HeaderStyle and
        // re-attach the menu. Otherwise right-clicking the Name header falls through to the
        // DataGrid's own context menu.
        foreach (DataGridColumn column in grid.Columns)
        {
            if (column.HeaderStyle is null)
            {
                continue;
            }

            Style columnHeaderStyle = new(typeof(DataGridColumnHeader), column.HeaderStyle);
            columnHeaderStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, _headerContextMenu));
            column.HeaderStyle = columnHeaderStyle;
        }
    }

    private static ContextMenu BuildColumnToggleMenu(DataGrid grid)
    {
        ContextMenu menu = new();

        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? "Column";
            MenuItem item = new()
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };

            // Keep the Name column visible — the group expander lives there.
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

    private void UrlText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || string.IsNullOrEmpty(tb.Text))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(tb.Text) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // Best-effort; failing silently is fine for a link click.
        }
    }

    /// <summary>
    /// Select the row (or all rows in a group) under the cursor on right-click so the
    /// context menu acts on that target. Preserves an existing multi-selection when the
    /// right-clicked row is already part of it.
    /// </summary>
    private void FilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;

        DataGridRow? row = FindAncestor<DataGridRow>(source);
        if (row is not null)
        {
            if (!row.IsSelected)
            {
                FilesGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }

            return;
        }

        // Right-clicked on a group header (the package bar) — select every row in the group
        // so Copy URL / Remove / Export / Copy operate on the whole package.
        GroupItem? groupItem = FindAncestor<GroupItem>(source);
        if (groupItem?.DataContext is CollectionViewGroup group)
        {
            FilesGrid.SelectedItems.Clear();
            foreach (object item in group.Items)
            {
                FilesGrid.SelectedItems.Add(item);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }
}
