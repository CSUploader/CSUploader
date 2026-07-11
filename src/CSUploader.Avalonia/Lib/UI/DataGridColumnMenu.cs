// <copyright file="DataGridColumnMenu.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Services;

namespace CSUploader.Lib.UI;

/// <summary>
/// Builds the show/hide column menu shared by every Phase 5 grid. The WPF head grew two
/// near-identical per-view builders (UploadedView.xaml.cs and LogsView.xaml.cs); the
/// Avalonia head shares ONE copy — the reset message/title keys are the only per-grid
/// difference, so they are parameters instead.
/// </summary>
internal static class DataGridColumnMenu
{
    /// <summary>
    /// Builds a checkable-per-column menu with a trailing "Reset columns" entry. Each toggle
    /// flips the column's <see cref="DataGridColumn.IsVisible"/> and persists immediately via
    /// <see cref="DataGridColumnVisibilityPersistence.PersistAsync"/> (no separate save action);
    /// Reset prompts through the standard opt-out confirmation, then restores the captured
    /// <paramref name="defaults"/> and clears the persisted row.
    /// </summary>
    /// <param name="grid">The grid whose columns the menu toggles.</param>
    /// <param name="defaults">The XAML-default column state captured before <c>ApplyAsync</c>.</param>
    /// <param name="repo">Setting store the toggles persist into.</param>
    /// <param name="settingKey">The per-grid setting key.</param>
    /// <param name="dialogService">Confirms the Reset action via the opt-out prompt.</param>
    /// <param name="resetMessageKey">ResX key for the Reset confirmation message.</param>
    /// <param name="resetTitleKey">ResX key for the Reset confirmation title.</param>
    public static ContextMenu Build(
        DataGrid grid,
        Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState> defaults,
        SettingRepository repo,
        string settingKey,
        IDialogService dialogService,
        string resetMessageKey,
        string resetTitleKey)
    {
        ContextMenu menu = new();

        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? Localizer.Instance["Uploads_ColumnMenu_DefaultLabel"];
            MenuItem item = new()
            {
                Header = header,

                // Unlike WPF's IsCheckable, Avalonia renders NO check glyph from IsChecked
                // alone — ToggleType=CheckBox is what draws the checkmark (port rule 31).
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                StaysOpenOnClick = true,
            };

            // Keep the first column visible — it's the anchor that guarantees at least one
            // header stays right-clickable to reopen this menu (and on the grouped grid it
            // carries the group expander).
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
                    capturedColumn.IsVisible = capturedItem.IsChecked;

                    // Persist immediately so the user doesn't need a separate save action.
                    await DataGridColumnVisibilityPersistence.PersistAsync(grid, repo, settingKey);
                };
            }

            menu.Items.Add(item);
        }

        // Reset entry — restores columns to their XAML-default visibility + order and
        // clears the persisted overrides so the next launch starts clean. Confirmed via
        // the standard opt-out prompt.
        menu.Items.Add(new Separator());
        MenuItem resetItem = new() { Header = Localizer.Instance["Uploads_ColumnMenu_Reset"] };
        resetItem.Click += async (_, _) =>
        {
            if (!await dialogService.ShowOptOutConfirmationAsync(
                    ConfirmationKeys.ResetColumns,
                    Localizer.Instance[resetMessageKey],
                    Localizer.Instance[resetTitleKey]))
            {
                return;
            }

            await DataGridColumnVisibilityPersistence.ResetAsync(grid, defaults, repo, settingKey);
        };
        menu.Items.Add(resetItem);

        // Refresh checkmarks each time the menu opens in case visibility changed elsewhere.
        // The first grid.Columns.Count items are the per-column entries in collection order;
        // the trailing Separator + Reset are left untouched.
        menu.Opened += (_, _) =>
        {
            for (int i = 0; i < menu.Items.Count && i < grid.Columns.Count; i++)
            {
                if (menu.Items[i] is MenuItem mi)
                {
                    mi.IsChecked = grid.Columns[i].IsVisible;
                }
            }
        };

        return menu;
    }

    /// <summary>
    /// Opens <paramref name="menu"/> when a column header is right-clicked. Avalonia has no
    /// per-column HeaderStyle-with-ContextMenu, so this replaces the WPF cloned-header-style
    /// trick; a TUNNEL <see cref="Control.ContextRequestedEvent"/> handler fires before the
    /// grid's own row context menu, so marking the header case handled ALSO guarantees the row
    /// menu never opens on the headers (the WPF <c>ContextMenuOpening</c> header pass-through).
    /// </summary>
    /// <remarks>
    /// do NOT also assign this menu as any control's ContextMenu — Open(header) throws if the
    /// menu is attached elsewhere.
    /// </remarks>
    public static void AttachToHeaders(DataGrid grid, ContextMenu menu)
    {
        grid.AddHandler(
            Control.ContextRequestedEvent,
            (object? _, ContextRequestedEventArgs e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>() is { } header)
                {
                    menu.Open(header);
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel);
    }
}
