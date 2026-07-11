// <copyright file="DataGridBehaviorTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Behaviors;

namespace CSUploader.Tests.Avalonia.Behaviors;

/// <summary>
/// Covers the two DataGrid behaviors (<see cref="DataGridSelectionBehaviors"/>,
/// <see cref="AutoScrollBehavior"/>). Two complementary layers:
/// <list type="bullet">
/// <item>Real input simulation against a shown headless window (the plan's primary path) — a
/// synthesized right/left <c>PointerPressed</c> at a realized row/header/empty-area point, asserting
/// the resulting <c>SelectedItems</c>. This is the closest headless analogue of the WPF gesture; full
/// interaction verification still lands with the first consuming grid in Phase 5.</item>
/// <item>Teardown/leak assertions that do NOT depend on hit-testing: the selection behaviors are proven
/// to add exactly one <c>PointerPressed</c> subscription on enable and remove it on disable (reflected
/// from <see cref="Interactive"/>'s handler store), and <see cref="AutoScrollBehavior"/> is proven to
/// hold exactly one <c>CollectionChanged</c> subscription that follows <c>ItemsSource</c> swaps and
/// releases cleanly — the WPF original never unsubscribed (Task 8, Task 7 leak lesson).</item>
/// </list>
/// </summary>
public class DataGridBehaviorTests
{
    private sealed record RowItem(string Name);

    // ── Selection behaviors: real input simulation (primary path) ──

    [AvaloniaFact]
    public void RightClick_OnUnselectedRow_SelectsExactlyThatRow()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(3);
        try
        {
            grid.SelectedItems.Add(items[0]);
            DataGridSelectionBehaviors.SetSelectRowOnRightClick(grid, true);
            Dispatcher.UIThread.RunJobs();

            DataGridRow row = RealizedRowOrThrow(grid, items[2]);
            window.MouseDown(CenterInWindow(row, window), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new object[] { items[2] }, grid.SelectedItems.Cast<object>().ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClick_InsideSelection_PreservesMultiSelection()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(3);
        try
        {
            grid.SelectedItems.Add(items[0]);
            grid.SelectedItems.Add(items[1]);
            DataGridSelectionBehaviors.SetSelectRowOnRightClick(grid, true);
            Dispatcher.UIThread.RunJobs();

            DataGridRow row = RealizedRowOrThrow(grid, items[1]);
            window.MouseDown(CenterInWindow(row, window), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            // Right-click inside the existing selection must leave the whole multi-selection intact.
            Assert.Equal(2, grid.SelectedItems.Count);
            Assert.Contains(items[0], grid.SelectedItems.Cast<object>());
            Assert.Contains(items[1], grid.SelectedItems.Cast<object>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LeftClick_OnEmptyArea_ClearsSelection()
    {
        // A 300px-tall grid with only 2 short rows leaves a wide empty band below the last row.
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(2);
        try
        {
            grid.SelectedItems.Add(items[0]);
            grid.SelectedItems.Add(items[1]);
            DataGridSelectionBehaviors.SetClearSelectionOnEmptyClick(grid, true);
            Dispatcher.UIThread.RunJobs();

            // A point low in the grid, clear of both rows and the (right-edge) vertical scrollbar.
            Point empty = grid.TranslatePoint(new Point(120, 260), window) ?? default;
            window.MouseDown(empty, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(grid.SelectedItems);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LeftClick_OnHeader_PreservesSelection()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(2);
        try
        {
            grid.SelectedItems.Add(items[0]);
            DataGridSelectionBehaviors.SetClearSelectionOnEmptyClick(grid, true);
            Dispatcher.UIThread.RunJobs();

            DataGridColumnHeader header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First();
            window.MouseDown(CenterInWindow(header, window), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // Header clicks (sorting) must not drop the selection.
            Assert.Single(grid.SelectedItems);
            Assert.Contains(items[0], grid.SelectedItems.Cast<object>());
        }
        finally
        {
            window.Close();
        }
    }

    // ── Selection behaviors: teardown (hit-testing-independent) ──

    [AvaloniaFact]
    public void SelectRowOnRightClick_TogglingOff_RemovesTheHandler()
    {
        var grid = new DataGrid();
        int baseline = PointerPressedHandlerCount(grid);

        DataGridSelectionBehaviors.SetSelectRowOnRightClick(grid, true);
        Assert.Equal(baseline + 1, PointerPressedHandlerCount(grid));

        DataGridSelectionBehaviors.SetSelectRowOnRightClick(grid, false);
        Assert.Equal(baseline, PointerPressedHandlerCount(grid));
    }

    [AvaloniaFact]
    public void ClearSelectionOnEmptyClick_TogglingOff_RemovesTheHandler()
    {
        var grid = new DataGrid();
        int baseline = PointerPressedHandlerCount(grid);

        DataGridSelectionBehaviors.SetClearSelectionOnEmptyClick(grid, true);
        Assert.Equal(baseline + 1, PointerPressedHandlerCount(grid));

        DataGridSelectionBehaviors.SetClearSelectionOnEmptyClick(grid, false);
        Assert.Equal(baseline, PointerPressedHandlerCount(grid));
    }

    // ── Chrome walk helper (unit-level; the fallback surface if a hit-test case ever regresses) ──

    [AvaloniaFact]
    public void FindOwnChromeAncestor_StopsAtRow_AndReturnsNullForBareChrome()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(2);
        try
        {
            Dispatcher.UIThread.RunJobs();

            DataGridRow row = RealizedRowOrThrow(grid, items[0]);
            // A visual inside a realized row walks up to that row.
            Visual leaf = row.GetVisualDescendants().OfType<Visual>().LastOrDefault() ?? row;
            Assert.Same(row, DataGridSelectionBehaviors.FindOwnChromeAncestor(leaf, grid));

            // The grid itself is the walk's terminator, not chrome → null.
            Assert.Null(DataGridSelectionBehaviors.FindOwnChromeAncestor(grid, grid));
        }
        finally
        {
            window.Close();
        }
    }

    // ── AutoScrollBehavior ──

    [AvaloniaFact]
    public void AutoScroll_AddToBoundCollection_ScrollsWithoutThrowing()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildGrid(3);
        try
        {
            // The grid's own DataGridCollectionView subscribes to the source too, so count the
            // BEHAVIOR's contribution as the delta over that baseline.
            int gridOnly = CollectionChangedSubscriberCount(items);
            AutoScrollBehavior.SetIsEnabled(grid, true);
            Assert.Equal(gridOnly + 1, CollectionChangedSubscriberCount(items));

            for (int i = 4; i <= 40; i++)
            {
                items.Add(new RowItem($"item {i}"));
            }

            // The Add handler calls ScrollIntoView on each append; reaching here proves it drove that
            // without throwing, and the behavior still holds exactly its one subscription after growth.
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(gridOnly + 1, CollectionChangedSubscriberCount(items));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AutoScroll_Enable_Disable_LeavesNoSubscription()
    {
        var items = new ObservableCollection<RowItem> { new("a"), new("b"), new("c") };
        var grid = new DataGrid { ItemsSource = items };

        // The grid already holds its own collection-view subscription; the behavior's contribution is
        // the delta on enable, which must return to zero on disable (the WPF never-unsubscribe leak).
        int gridOnly = CollectionChangedSubscriberCount(items);

        AutoScrollBehavior.SetIsEnabled(grid, true);
        Assert.Equal(gridOnly + 1, CollectionChangedSubscriberCount(items));

        AutoScrollBehavior.SetIsEnabled(grid, false);
        Assert.Equal(gridOnly, CollectionChangedSubscriberCount(items)); // behavior's subscription gone
    }

    [AvaloniaFact]
    public void AutoScroll_ItemsSourceSwap_TracksNewCollectionAndReleasesOld()
    {
        var first = new ObservableCollection<RowItem> { new("a1") };
        var second = new ObservableCollection<RowItem> { new("b1") };
        var grid = new DataGrid { ItemsSource = first };

        AutoScrollBehavior.SetIsEnabled(grid, true);

        grid.ItemsSource = second;
        Dispatcher.UIThread.RunJobs();

        // Read each collection's count with the behavior on, then toggle it off and read again: the
        // per-collection delta IS the behavior's contribution, isolated from the grid's own view sub.
        int firstWithBehavior = CollectionChangedSubscriberCount(first);
        int secondWithBehavior = CollectionChangedSubscriberCount(second);
        AutoScrollBehavior.SetIsEnabled(grid, false);
        int firstWithout = CollectionChangedSubscriberCount(first);
        int secondWithout = CollectionChangedSubscriberCount(second);

        // Behaviour followed the swap onto `second` (delta 1) and had already released `first` (delta 0).
        Assert.Equal(secondWithout + 1, secondWithBehavior);
        Assert.Equal(firstWithout, firstWithBehavior);
    }

    // ── Helpers ──

    private static (Window Window, DataGrid Grid, ObservableCollection<RowItem> Items) BuildGrid(int count)
    {
        var items = new ObservableCollection<RowItem>(
            Enumerable.Range(1, count).Select(i => new RowItem($"item {i}")));
        var grid = new DataGrid
        {
            ItemsSource = items,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            // A non-null background makes the empty region below the rows hit-testable (null
            // backgrounds are transparent to hit-testing) — the ClearSelectionOnEmptyClick target;
            // a themed consuming grid supplies one in the real app.
            Background = Brushes.Transparent,
            Width = 400,
            Height = 300,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(RowItem.Name)) });

        var window = new Window { Width = 420, Height = 340, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid, items);
    }

    private static DataGridRow RealizedRowOrThrow(DataGrid grid, object item)
    {
        DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>()
            .FirstOrDefault(r => ReferenceEquals(r.DataContext, item));
        Assert.True(row is not null, "the DataGridRow did not realize headlessly — fall back to the Phase 5 interaction checklist");
        return row!;
    }

    private static Point CenterInWindow(Visual v, Visual window) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window) ?? default;

    // Delta from baseline is what matters: the DataGrid registers its own PointerPressed handlers, so
    // only the change on enable/disable is attributable to the behavior.
    private static int PointerPressedHandlerCount(Interactive obj)
    {
        FieldInfo field = typeof(Interactive).GetField("_eventHandlers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(obj) is not IDictionary map)
        {
            return 0;
        }

        foreach (DictionaryEntry entry in map)
        {
            if (ReferenceEquals(entry.Key, InputElement.PointerPressedEvent))
            {
                return ((ICollection)entry.Value!).Count;
            }
        }

        return 0;
    }

    // The compiler-generated backing field of ObservableCollection's field-like CollectionChanged
    // event; its invocation-list length is the number of live subscribers (same trick as the
    // Localizer leak test).
    private static int CollectionChangedSubscriberCount(INotifyCollectionChanged source)
    {
        FieldInfo field = source.GetType().GetField(
            "CollectionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (NotifyCollectionChangedEventHandler?)field.GetValue(source);
        return handler?.GetInvocationList().Length ?? 0;
    }
}
