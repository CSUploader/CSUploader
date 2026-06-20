// <copyright file="UploadsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CSUploader.Lib.Localization;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class UploadsView : UserControl
{
    private ContextMenu? _headerContextMenu;

    // Snapshot of the XAML-default column state, captured at first Loaded before any
    // persisted overrides are applied. Used by the "Reset columns" menu entry to put
    // the grid back the way the developer shipped it.
    private Dictionary<string, Lib.UI.DataGridColumnVisibilityPersistence.ColumnState>? _defaultColumnState;

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

    private async void UploadsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || _headerContextMenu is not null)
        {
            return;
        }

        // Override the default Copy command so Package rows pull in their children. The
        // built-in DataGrid TSV builder only serializes the rows in SelectedItems, so a
        // user copying a single Package row otherwise gets just the package and none of
        // its files. The custom handler expands the selection in-memory before formatting.
        grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (s, args) => OnCopyWithChildrenExecuted(grid, args)));

        // Capture XAML defaults *before* applying persisted overrides so "Reset columns"
        // can restore them later.
        _defaultColumnState = Lib.UI.DataGridColumnVisibilityPersistence.CaptureCurrentState(grid);

        // Apply the user's persisted column visibility + display order before building
        // the menu so its IsChecked state lines up with the grid.
        if (DataContext is UploadsViewModel vm && vm.SettingRepo is { } repo)
        {
            await Lib.UI.DataGridColumnVisibilityPersistence.ApplyAsync(grid, repo, SettingKey.UploadsTabHiddenColumns);
        }

        _headerContextMenu = BuildColumnToggleMenu(grid);

        // Apply the menu to every column header via style (shared across headers).
        var headerStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
        headerStyle.Setters.Add(new Setter(ContextMenuProperty, _headerContextMenu));
        grid.ColumnHeaderStyle = headerStyle;

        // Persist any column reorder the user does after this point. Apply itself sets
        // DisplayIndex which would also fire this event, so the subscription happens
        // after the initial Apply finishes.
        grid.ColumnDisplayIndexChanged += async (_, _) =>
        {
            if (DataContext is UploadsViewModel innerVm && innerVm.SettingRepo is { } innerRepo)
            {
                await Lib.UI.DataGridColumnVisibilityPersistence.PersistAsync(grid, innerRepo, SettingKey.UploadsTabHiddenColumns);
            }
        };
    }

    private ContextMenu BuildColumnToggleMenu(DataGrid grid)
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

            // Name column is the anchor for the expand toggle — don't allow hiding.
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
                    capturedColumn.Visibility = capturedItem.IsChecked
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    // Persist immediately so the user doesn't need a separate save action.
                    if (DataContext is UploadsViewModel vm && vm.SettingRepo is { } repo)
                    {
                        await Lib.UI.DataGridColumnVisibilityPersistence.PersistAsync(grid, repo, SettingKey.UploadsTabHiddenColumns);
                    }
                };
            }

            menu.Items.Add(item);
        }

        // Reset entry — restores columns to their XAML-default visibility + order and
        // clears the persisted overrides so the next launch starts clean. Confirmed via
        // the standard opt-out prompt so an accidental click doesn't blow away an
        // elaborately-tuned column layout silently.
        menu.Items.Add(new Separator());
        MenuItem resetItem = new() { Header = Localizer.Instance["Uploads_ColumnMenu_Reset"] };
        resetItem.Click += async (_, _) =>
        {
            if (_defaultColumnState is null
                || DataContext is not UploadsViewModel vm
                || vm.SettingRepo is not { } repo)
            {
                return;
            }

            if (!vm.DialogServiceForView.ShowOptOutConfirmation(
                    Services.ConfirmationKeys.ResetColumns,
                    Localizer.Instance["Uploads_ResetColumns_Message"],
                    Localizer.Instance["Uploads_ResetColumns_Title"]))
            {
                return;
            }

            await Lib.UI.DataGridColumnVisibilityPersistence.ResetAsync(
                grid, _defaultColumnState, repo, SettingKey.UploadsTabHiddenColumns);
        };
        menu.Items.Add(resetItem);

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

    /// <summary>
    /// Commits an edited "Order" cell to a move. The editing TextBox holds the raw typed
    /// 1-based position; SetOrderCommand routes it through the package manager, which
    /// clamps and re-numbers. Package and terminal rows are ignored.
    /// </summary>
    private void UploadsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (e.Column != OrderColumn)
        {
            return;
        }

        if (e.Row.Item is not PackageFile file)
        {
            return; // ignore package rows
        }

        // For a DataGridTemplateColumn the editing element IS the TextBox from the
        // CellEditingTemplate, so no visual-tree walk is needed.
        if (e.EditingElement is not TextBox tb || !int.TryParse(tb.Text, out int target))
        {
            return;
        }

        if (DataContext is UploadsViewModel vm)
        {
            vm.SetOrderCommand.Execute((file, target));
        }
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
    /// Toggles the Upload Overview's stats row in/out via the chevron next to the title.
    /// The full panel is hidden by the ✕ button (which sets ShowUploadOverview=false);
    /// this handler only collapses the stats area, leaving the title bar visible.
    /// </summary>
    private void OverviewToggle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is UploadsViewModel vm)
        {
            vm.IsOverviewExpanded = !vm.IsOverviewExpanded;
            e.Handled = true;
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
    /// Suppresses the DataGrid's context menu when the right-click landed on empty space
    /// below the rows. Every menu entry binds against the selected row, so opening it on
    /// whitespace would yield a non-functional menu.
    /// </summary>
    private void UploadsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;

        // Column headers have their own ContextMenu (the show/hide menu, set via the
        // cloned ColumnHeaderStyle in UploadsGrid_Loaded). The ContextMenuOpening event
        // bubbles up from the header to the DataGrid, so we must let it pass — otherwise
        // setting e.Handled below cancels the header's menu too.
        if (FindAncestor<DataGridColumnHeader>(source) is not null)
        {
            return;
        }

        // Snapshot the full multi-selection so per-column "Copy" acts on every selected row,
        // not just the primary SelectedRow. (SelectRowOnRightClick already ran on right-down,
        // so the selection is final here.)
        if (DataContext is UploadsViewModel vm)
        {
            vm.SelectedRows = [.. uploadsGrid.SelectedItems.Cast<object>()];
        }

        if (FindAncestor<DataGridRow>(source) is not null)
        {
            return;
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    /// <summary>
    /// Copies selected rows as TSV (with header). For any selected Package, also copies
    /// its child files immediately after the package row so the user gets the full
    /// hierarchy in one paste instead of just the aggregate row.
    /// </summary>
    private static void OnCopyWithChildrenExecuted(DataGrid grid, ExecutedRoutedEventArgs e)
    {
        object[] selection = grid.SelectedItems.Cast<object>().ToArray();
        if (selection.Length == 0)
        {
            return;
        }

        List<object> expanded = [];
        HashSet<object> seen = [];
        foreach (object item in selection)
        {
            if (!seen.Add(item))
            {
                continue;
            }

            expanded.Add(item);
            if (item is Package pkg)
            {
                foreach (PackageFile child in pkg)
                {
                    if (seen.Add(child))
                    {
                        expanded.Add(child);
                    }
                }
            }
        }

        DataGridColumn[] columns = [.. grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)];

        StringBuilder sb = new();
        if (grid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader)
        {
            sb.AppendLine(string.Join("\t", columns.Select(c => c.Header?.ToString() ?? string.Empty)));
        }

        foreach (object item in expanded)
        {
            sb.AppendLine(string.Join("\t", columns.Select(c => EvaluateClipboardBinding(c.ClipboardContentBinding, item))));
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Clipboard.SetText can throw under contention with another app — swallow
            // rather than crash the UI thread for a copy operation.
        }

        e.Handled = true;
    }

    /// <summary>
    /// Evaluates a column's ClipboardContentBinding against a row item by routing the
    /// binding through a throwaway TextBlock. The DataGrid's own copy implementation
    /// uses the same binding pipeline; mirroring it here keeps converters / formatters
    /// honoured without re-implementing them in code.
    /// </summary>
    private static string EvaluateClipboardBinding(BindingBase? binding, object item)
    {
        if (binding is null)
        {
            return string.Empty;
        }

        TextBlock tb = new() { DataContext = item };
        tb.SetBinding(TextBlock.TextProperty, binding);
        string result = tb.Text ?? string.Empty;
        BindingOperations.ClearBinding(tb, TextBlock.TextProperty);
        return result;
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
