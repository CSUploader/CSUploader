// <copyright file="AutoScrollBehavior.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace CSUploader.Behaviors;

/// <summary>
/// Scrolls a <see cref="DataGrid"/> to its newest item whenever the bound collection grows
/// (the Logs tab's follow mode). Avalonia rebuild of <c>src/Behaviors/AutoScrollBehavior.cs</c>;
/// unlike the WPF original it tracks <see cref="DataGrid.ItemsSource"/> swaps and never leaks the
/// handler the WPF version left subscribed forever. It releases the collection subscription on THREE
/// occasions: on disable, on an <see cref="DataGrid.ItemsSource"/> swap, and on removal from the
/// visual tree — the last so a recreated view whose grid binds a singleton view-model collection
/// cannot pin the dead grid alive through that long-lived collection. It re-tracks on re-insertion
/// while still enabled.
/// </summary>
/// <remarks>
/// Non-static for the same owner-type reason as <see cref="DataGridSelectionBehaviors"/>.
/// </remarks>
public sealed class AutoScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<AutoScrollBehavior, DataGrid, bool>("IsEnabled");

    // Per-grid subscription state (the INCC handler currently attached, if any).
    private static readonly AttachedProperty<NotifyCollectionChangedEventHandler?> AttachedHandlerProperty =
        AvaloniaProperty.RegisterAttached<AutoScrollBehavior, DataGrid, NotifyCollectionChangedEventHandler?>("AttachedHandler");

    static AutoScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.PropertyChanged += Grid_PropertyChanged;
                grid.AttachedToVisualTree += Grid_AttachedToVisualTree;
                grid.DetachedFromVisualTree += Grid_DetachedFromVisualTree;
                Attach(grid, grid.ItemsSource);
            }
            else
            {
                grid.PropertyChanged -= Grid_PropertyChanged;
                grid.AttachedToVisualTree -= Grid_AttachedToVisualTree;
                grid.DetachedFromVisualTree -= Grid_DetachedFromVisualTree;
                Detach(grid, grid.ItemsSource);
            }
        });
    }

    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    private static void Grid_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is DataGrid grid && e.Property == DataGrid.ItemsSourceProperty)
        {
            Detach(grid, e.OldValue as IEnumerable);
            Attach(grid, e.NewValue as IEnumerable);
        }
    }

    // Removed from the visual tree (tab switch / view recreation): drop the collection subscription so a
    // long-lived (singleton view-model) source can't keep this now-dead grid alive.
    private static void Grid_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            Detach(grid, grid.ItemsSource);
        }
    }

    // Re-inserted while still enabled: re-track the current source (the enable handler's Attach ran when
    // the grid first turned on, but the intervening detach released it).
    private static void Grid_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DataGrid grid && GetIsEnabled(grid))
        {
            Attach(grid, grid.ItemsSource);
        }
    }

    private static void Attach(DataGrid grid, IEnumerable? source)
    {
        // Idempotent: a grid already tracking its current source (e.g. enabled before it entered the
        // visual tree, now receiving its first AttachedToVisualTree) must not subscribe a second handler.
        if (source is not INotifyCollectionChanged incc || grid.GetValue(AttachedHandlerProperty) is not null)
        {
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && grid.ItemsSource is IList { Count: > 0 } list)
            {
                grid.ScrollIntoView(list[^1], null);
            }
        };
        incc.CollectionChanged += handler;
        grid.SetValue(AttachedHandlerProperty, handler);
    }

    private static void Detach(DataGrid grid, IEnumerable? source)
    {
        if (source is INotifyCollectionChanged incc && grid.GetValue(AttachedHandlerProperty) is { } handler)
        {
            incc.CollectionChanged -= handler;
            grid.SetValue(AttachedHandlerProperty, null);
        }
    }
}
