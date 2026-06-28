// <copyright file="DataGridSelectionBehaviors.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CSUploader.Behaviors;

/// <summary>
/// Attached behaviors that make <see cref="DataGrid"/> selection match conventional
/// Windows / Explorer UX. Apply by setting one or both attached properties on the
/// DataGrid in XAML — they're independent and can be turned on individually.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a behavior instead of code-behind:</b> the two handlers are mechanical
/// visual-tree walks identical across every grid that wants the UX. Without a behavior,
/// every grid's XAML.cs would have to redeclare them. The first two grids (accountsGrid,
/// proxyGrid) used inline handlers and got copy-paste rot fast — this consolidates them.
/// </para>
/// <para>
/// XAML usage:
/// <code>
/// xmlns:beh="clr-namespace:CSUploader.Behaviors"
/// ...
/// &lt;DataGrid x:Name="myGrid"
///           beh:DataGridSelectionBehaviors.ClearSelectionOnEmptyClick="True"
///           beh:DataGridSelectionBehaviors.SelectRowOnRightClick="True" /&gt;
/// </code>
/// </para>
/// </remarks>
public static class DataGridSelectionBehaviors
{
    // ── ClearSelectionOnEmptyClick ──
    //
    // Left-click inside the DataGrid's chrome but OUTSIDE any data row (e.g. the empty
    // area below the last row) clears the selection. Clicks on rows are passed through
    // so WPF's normal row-selection logic fires; clicks on column headers preserve the
    // selection so sorting via header-click doesn't drop the user's selection; clicks on
    // scrollbars preserve the selection so scrolling doesn't drop it either.

    public static readonly DependencyProperty ClearSelectionOnEmptyClickProperty =
        DependencyProperty.RegisterAttached(
            "ClearSelectionOnEmptyClick",
            typeof(bool),
            typeof(DataGridSelectionBehaviors),
            new PropertyMetadata(false, OnClearSelectionOnEmptyClickChanged));

    public static void SetClearSelectionOnEmptyClick(DependencyObject d, bool value) => d.SetValue(ClearSelectionOnEmptyClickProperty, value);

    public static bool GetClearSelectionOnEmptyClick(DependencyObject d) => (bool)d.GetValue(ClearSelectionOnEmptyClickProperty);

    private static void OnClearSelectionOnEmptyClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.MouseDown += OnMouseDown_ClearIfEmpty;
        }
        else
        {
            grid.MouseDown -= OnMouseDown_ClearIfEmpty;
        }
    }

    private static void OnMouseDown_ClearIfEmpty(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            switch (source)
            {
                case DataGridRow:
                case DataGridColumnHeader:
                case ScrollBar:
                    return;
            }
            source = VisualTreeHelper.GetParent(source);
        }

        grid.UnselectAll();
    }

    // ── SelectRowOnRightClick ──
    //
    // Right-clicking a row that's NOT already in the selection clears the selection and
    // selects just that row, so the context menu's commands target the row the user
    // actually right-clicked. Right-clicking a row that IS in the selection preserves
    // the whole multi-selection — matches Explorer behaviour for multi-select context
    // operations. Right-clicks on non-row chrome (empty area, header, scrollbar) leave
    // selection untouched.

    public static readonly DependencyProperty SelectRowOnRightClickProperty =
        DependencyProperty.RegisterAttached(
            "SelectRowOnRightClick",
            typeof(bool),
            typeof(DataGridSelectionBehaviors),
            new PropertyMetadata(false, OnSelectRowOnRightClickChanged));

    public static void SetSelectRowOnRightClick(DependencyObject d, bool value) => d.SetValue(SelectRowOnRightClickProperty, value);

    public static bool GetSelectRowOnRightClick(DependencyObject d) => (bool)d.GetValue(SelectRowOnRightClickProperty);

    private static void OnSelectRowOnRightClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.PreviewMouseRightButtonDown += OnPreviewRightDown_SelectRow;
        }
        else
        {
            grid.PreviewMouseRightButtonDown -= OnPreviewRightDown_SelectRow;
        }
    }

    private static void OnPreviewRightDown_SelectRow(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is not DataGridRow row || row.IsSelected)
        {
            return;
        }

        grid.UnselectAll();
        row.IsSelected = true;
    }
}
