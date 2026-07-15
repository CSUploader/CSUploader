// <copyright file="DataGridSelectionBehaviors.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CSUploader.Behaviors;

/// <summary>
/// Attached behaviors that make <see cref="DataGrid"/> selection match conventional
/// Windows / Explorer UX — Avalonia rebuild of the WPF behaviors
/// (src/Behaviors/DataGridSelectionBehaviors.cs); same two independent switches.
/// </summary>
/// <remarks>
/// <para>
/// The right-click handler is registered at TUNNEL phase so selection is already updated
/// when <c>ContextRequested</c> fires and the context menu's commands snapshot
/// <c>SelectedItems</c> — the port of the WPF <c>PreviewMouseRightButtonDown</c> →
/// <c>ContextMenuOpening</c> ordering guarantee (Avalonia has no
/// <c>PreviewMouseRightButtonDown</c>/<c>ContextMenuOpening</c>). Full interaction
/// verification lands when the first consuming grid ships (Phase 5).
/// </para>
/// <para>
/// Non-static class: Avalonia's generic <see cref="AvaloniaProperty.RegisterAttached{TOwner, THost, TValue}(string, TValue, bool, Avalonia.Data.BindingMode)"/>
/// needs a concrete owner type. XAML usage keeps the WPF namespace verbatim so ported
/// XAML lines port unchanged:
/// <code>
/// xmlns:beh="clr-namespace:CSUploader.Behaviors"
/// ...
/// &lt;DataGrid beh:DataGridSelectionBehaviors.ClearSelectionOnEmptyClick="True"
///           beh:DataGridSelectionBehaviors.SelectRowOnRightClick="True" /&gt;
/// </code>
/// </para>
/// </remarks>
public sealed class DataGridSelectionBehaviors
{
    public static readonly AttachedProperty<bool> ClearSelectionOnEmptyClickProperty =
        AvaloniaProperty.RegisterAttached<DataGridSelectionBehaviors, DataGrid, bool>("ClearSelectionOnEmptyClick");

    public static readonly AttachedProperty<bool> SelectRowOnRightClickProperty =
        AvaloniaProperty.RegisterAttached<DataGridSelectionBehaviors, DataGrid, bool>("SelectRowOnRightClick");

    static DataGridSelectionBehaviors()
    {
        ClearSelectionOnEmptyClickProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed_ClearIfEmpty, RoutingStrategies.Bubble);
            }
            else
            {
                grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed_ClearIfEmpty);
            }
        });

        SelectRowOnRightClickProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                // Tunnel: must beat both the grid's own selection handling and ContextRequested.
                grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed_SelectRowOnRight, RoutingStrategies.Tunnel);
            }
            else
            {
                grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed_SelectRowOnRight);
            }
        });
    }

    public static void SetClearSelectionOnEmptyClick(DataGrid grid, bool value) => grid.SetValue(ClearSelectionOnEmptyClickProperty, value);

    public static bool GetClearSelectionOnEmptyClick(DataGrid grid) => grid.GetValue(ClearSelectionOnEmptyClickProperty);

    public static void SetSelectRowOnRightClick(DataGrid grid, bool value) => grid.SetValue(SelectRowOnRightClickProperty, value);

    public static bool GetSelectRowOnRightClick(DataGrid grid) => grid.GetValue(SelectRowOnRightClickProperty);

    private static void OnPointerPressed_ClearIfEmpty(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A hit on a row keeps normal selection handling; header clicks (sorting) and
        // scrollbar clicks must not drop the selection either — mirror of the WPF walk.
        if (FindOwnChromeAncestor(e.Source as Visual, grid) is not null)
        {
            return;
        }

        grid.SelectedItems.Clear();
    }

    private static void OnPointerPressed_SelectRowOnRight(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
        {
            return;
        }

        DataGridRow? row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row is null || grid.SelectedItems.Contains(row.DataContext))
        {
            return; // right-click inside the selection preserves the multi-selection (Explorer UX)
        }

        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(row.DataContext);
    }

    /// <summary>
    /// Walks source→grid; returns the first row/header/scrollbar hit, else null. Internal so
    /// the headless tests can pin the walk without synthesizing pointer events.
    /// </summary>
    internal static Visual? FindOwnChromeAncestor(Visual? source, DataGrid grid)
    {
        for (Visual? v = source; v is not null && v != grid; v = v.GetVisualParent())
        {
            if (v is DataGridRow or DataGridColumnHeader or ScrollBar)
            {
                return v;
            }
        }

        return null;
    }
}
