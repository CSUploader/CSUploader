// <copyright file="UploadsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
