// <copyright file="DataGridEditBehaviors.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

namespace CSUploader.Behaviors;

/// <summary>
/// Attached behavior for an editable <see cref="DataGrid"/> whose collection view outlives a tab switch:
/// when the grid comes back into the window, it closes the edit the grid has let go of but its view still
/// holds open.
/// </summary>
/// <remarks>
/// <para>
/// A grid taken out of the window in the middle of a row edit — its tab left with a cell still being
/// edited — cancels that edit when it is put back (<c>DataGrid.InitializeElements</c>, DataGrid 12.1.2). A row
/// that is not an <see cref="IEditableObject"/> cannot cancel an edit, so the grid puts the cell back and
/// forgets the edit while its <see cref="DataGridCollectionView"/> keeps it open. A view with an edit open
/// throws when asked to refresh, re-sort or re-filter, so whatever the grid did through its view failed
/// from then on — for the proxy grid, its column sort and its ▲/▼ buttons — until an edit started
/// somewhere in the grid. Back when every tab visit gave the grid a new view, the stuck one was thrown
/// away with the visit. (The Uploads grid does not need this: the only thing it does through its view is
/// filter, and its filter handler closes such an edit itself.)
/// </para>
/// <para>
/// Closing the edit at the view loses nothing: the grid has already put the cell back. It runs from
/// <see cref="Visual.AttachedToVisualTree"/>, which Avalonia raises after the grid's own attach handling,
/// so the grid has let go of the edit by then.
/// </para>
/// <code>
/// &lt;DataGrid beh:DataGridEditBehaviors.EndDroppedEdits="True" /&gt;
/// </code>
/// </remarks>
public sealed class DataGridEditBehaviors
{
    public static readonly AttachedProperty<bool> EndDroppedEditsProperty =
        AvaloniaProperty.RegisterAttached<DataGridEditBehaviors, DataGrid, bool>("EndDroppedEdits");

    static DataGridEditBehaviors()
    {
        EndDroppedEditsProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            grid.AttachedToVisualTree -= OnAttachedToVisualTree;
            if (e.NewValue is true)
            {
                grid.AttachedToVisualTree += OnAttachedToVisualTree;
            }
        });
    }

    public static void SetEndDroppedEdits(DataGrid grid, bool value) => grid.SetValue(EndDroppedEditsProperty, value);

    public static bool GetEndDroppedEdits(DataGrid grid) => grid.GetValue(EndDroppedEditsProperty);

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DataGrid { CollectionView: DataGridCollectionView { IsEditingItem: true, IsAddingNew: false } view })
        {
            view.CommitEdit();
        }
    }
}
