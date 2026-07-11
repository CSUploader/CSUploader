// <copyright file="DataGridZebraStriping.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;

namespace CSUploader.Behaviors;

/// <summary>
/// Alternating-row shading for a <see cref="DataGrid"/>. Avalonia has no
/// <c>AlternatingRowBackground</c>, so this is the design-pinned replacement (port rule 21):
/// on every row load it toggles the <c>alt</c> style class from the row's current index, and
/// consumers carry the brush in a view style <c>DataGridRow.alt { Background = … }</c>
/// (<c>LogAltRowBrush</c> for the log grids, <c>DataGridAltRowBrush</c> for UploadedView —
/// both already in ThemeBrushes.axaml, light + dark).
/// </summary>
/// <remarks>
/// <para>
/// Shared by all five Phase 5 grids (the four LogsView grids + the grouped UploadedView).
/// Because <see cref="DataGrid.LoadingRow"/> fires on every (re)bind of a recycled container,
/// setting the class from the CURRENT <see cref="DataGridRow.Index"/> is inherently
/// recycling-safe; <see cref="DataGrid.UnloadingRow"/> clears it as belt-and-braces.
/// <see cref="DataGridRow.Index"/> numbers rows flat across groups (Task 2 probe, checklist 7 —
/// <c>DataGridRow.GetIndex()</c> is <c>[Obsolete]</c> on 11.3.13), so the same
/// <c>index % 2</c> basis serves both the flat log grids and the grouped UploadedView.
/// </para>
/// <para>
/// Non-static for the same owner-type reason as <see cref="DataGridSelectionBehaviors"/> and
/// <see cref="AutoScrollBehavior"/>. The handlers are on the grid's OWN events, so enable/disable
/// simply subscribes/unsubscribes — no long-lived collection can pin the grid through them.
/// XAML usage keeps the WPF-style behaviors namespace:
/// <code>
/// xmlns:beh="clr-namespace:CSUploader.Behaviors"
/// ...
/// &lt;DataGrid beh:DataGridZebraStriping.IsEnabled="True" /&gt;
/// </code>
/// </para>
/// </remarks>
public sealed class DataGridZebraStriping
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGridZebraStriping, DataGrid, bool>("IsEnabled");

    static DataGridZebraStriping()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.LoadingRow += OnLoadingRow;
                grid.UnloadingRow += OnUnloadingRow;
            }
            else
            {
                grid.LoadingRow -= OnLoadingRow;
                grid.UnloadingRow -= OnUnloadingRow;
            }
        });
    }

    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    // LoadingRow fires on every (re)bind of a recycled container, so setting the class from the
    // CURRENT index is inherently recycling-safe; UnloadingRow clears it as belt-and-braces.
    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e)
        => e.Row.Classes.Set("alt", e.Row.Index % 2 == 1);

    private static void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
        => e.Row.Classes.Set("alt", false);
}
