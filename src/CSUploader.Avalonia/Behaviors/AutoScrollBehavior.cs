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
/// unlike the WPF original it tracks <see cref="DataGrid.ItemsSource"/> swaps and detaches
/// cleanly on disable — the WPF version subscribed the auto-tracking <c>Items</c> collection and
/// never unsubscribed, leaking a handler onto every bound collection for the process lifetime.
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
                Attach(grid, grid.ItemsSource);
                grid.PropertyChanged += Grid_PropertyChanged;
            }
            else
            {
                grid.PropertyChanged -= Grid_PropertyChanged;
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

    private static void Attach(DataGrid grid, IEnumerable? source)
    {
        if (source is not INotifyCollectionChanged incc)
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
