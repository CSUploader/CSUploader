// <copyright file="DataGridZebraStripingTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Behaviors;

namespace CSUploader.Tests.Avalonia.Behaviors;

/// <summary>
/// Covers the shared <see cref="DataGridZebraStriping"/> helper (Phase 5 Task 4, prep item 8) — the
/// Avalonia replacement for WPF's <c>AlternatingRowBackground</c> (port rule 21). The contract: on
/// every row (re)load, odd rows by current <see cref="DataGridRow.Index"/> carry the <c>alt</c> style
/// class (and thus the consumer's alt background); even rows do not; the class is set from the CURRENT
/// index so recycled containers re-stripe correctly; <c>UnloadingRow</c> clears it. The index basis is
/// flat across groups (Task 2 probe checklist 7), so the same helper serves the flat log grids and the
/// grouped UploadedView.
/// </summary>
public class DataGridZebraStripingTests
{
    // A distinctive brush instance so an assertion can prove the style resolved (Same == the setter's
    // exact instance), not merely that some background exists.
    private static readonly IBrush AltBrush = new SolidColorBrush(Color.Parse("#FF112233"));

    private sealed record RowItem(string Name);

    private sealed record GroupRow(string Group, string Name);

    // ── Even/odd assignment: the class AND the consumer's alt background ──

    [AvaloniaFact]
    public void Enabled_OddRowsGetAltClassAndBackground_EvenRowsDoNot()
    {
        (Window window, DataGrid grid, _) = BuildStripedGrid(6);
        try
        {
            foreach (DataGridRow row in RealizedRows(grid))
            {
                bool shouldBeAlt = row.Index % 2 == 1;
                Assert.Equal(shouldBeAlt, row.Classes.Contains("alt"));

                if (shouldBeAlt)
                {
                    // Odd rows resolve the view's DataGridRow.alt style → the alt brush instance.
                    Assert.Same(AltBrush, row.Background);
                }
                else
                {
                    Assert.NotSame(AltBrush, row.Background);
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── Adding a row keeps alternation parity (LoadingRow stripes the appended row) ──

    [AvaloniaFact]
    public void AddingRow_KeepsAlternationParity()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildStripedGrid(6);
        try
        {
            items.Add(new RowItem("item 7")); // index 6 → even → no alt
            Dispatcher.UIThread.RunJobs();
            AssertEveryRealizedRowStripedByIndex(grid);

            items.Add(new RowItem("item 8")); // index 7 → odd → alt
            Dispatcher.UIThread.RunJobs();
            AssertEveryRealizedRowStripedByIndex(grid);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Disable removes the handlers: rows loaded afterwards are never striped ──

    [AvaloniaFact]
    public void Disabled_NewRowsAreUnclassed()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildStripedGrid(6);
        try
        {
            DataGridZebraStriping.SetIsEnabled(grid, false);

            items.Add(new RowItem("item 7")); // index 6 (even)
            items.Add(new RowItem("item 8")); // index 7 — WOULD be alt if the handler were still live
            Dispatcher.UIThread.RunJobs();

            DataGridRow oddRow = RealizedRowFor(grid, items[7]);
            Assert.Equal(7, oddRow.Index);
            Assert.False(oddRow.Classes.Contains("alt"), "LoadingRow handler should be gone after disable");
        }
        finally
        {
            window.Close();
        }
    }

    // ── UnloadingRow clears the class (belt-and-braces for container recycling) ──

    [AvaloniaFact]
    public void Unloading_ClearsAltClass()
    {
        (Window window, DataGrid grid, ObservableCollection<RowItem> items) = BuildStripedGrid(6);
        try
        {
            // Capture the container instances that currently carry the class, then unload them all by
            // dropping the source. Each unloaded container must have its 'alt' class cleared.
            List<DataGridRow> wereAlt = RealizedRows(grid).Where(r => r.Classes.Contains("alt")).ToList();
            Assert.NotEmpty(wereAlt);

            grid.ItemsSource = null;
            Dispatcher.UIThread.RunJobs();

            foreach (DataGridRow row in wereAlt)
            {
                Assert.False(row.Classes.Contains("alt"), "UnloadingRow should have cleared the class");
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── Grouped grid: the index basis stripes flat across groups (probe fixture shape) ──

    [AvaloniaFact]
    public void GroupedGrid_StripesFlatAcrossGroups()
    {
        // The Task 2 probe shape in miniature: 3 groups of uneven size (3, 2, 2). DataGridRow.Index
        // numbers the file rows flat 0..6 across groups (no header gap), so the helper's index % 2
        // basis alternates continuously through the whole list — group boundaries don't reset it.
        var rows = new[]
        {
            new GroupRow("A", "a1"), new GroupRow("A", "a2"), new GroupRow("A", "a3"),
            new GroupRow("B", "b1"), new GroupRow("B", "b2"),
            new GroupRow("C", "c1"), new GroupRow("C", "c2"),
        };
        var view = new DataGridCollectionView(rows);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(GroupRow.Group)));

        (Window window, DataGrid grid) = ShowGrid(view);
        try
        {
            List<DataGridRow> dataRows = RealizedRows(grid).Where(r => r.DataContext is GroupRow).ToList();

            // All 7 file rows realize, indexed flat 0..6 regardless of the 3 group boundaries.
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, dataRows.Select(r => r.Index).OrderBy(i => i));
            foreach (DataGridRow row in dataRows)
            {
                Assert.Equal(row.Index % 2 == 1, row.Classes.Contains("alt"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── Helpers ──

    private static (Window Window, DataGrid Grid, ObservableCollection<RowItem> Items) BuildStripedGrid(int count)
    {
        var items = new ObservableCollection<RowItem>(
            Enumerable.Range(1, count).Select(i => new RowItem($"item {i}")));
        (Window window, DataGrid grid) = ShowGrid(items);
        return (window, grid, items);
    }

    private static (Window Window, DataGrid Grid) ShowGrid(IEnumerable itemsSource)
    {
        var grid = new DataGrid
        {
            ItemsSource = itemsSource,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brushes.Transparent,
            Width = 400,
            Height = 300,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Name") });
        DataGridZebraStriping.SetIsEnabled(grid, true);

        // The consumer-supplied style the real views carry (ThemeBrushes' DataGridAltRowBrush /
        // LogAltRowBrush); here a distinctive instance so the resolution is unambiguous.
        var altStyle = new Style(x => x.OfType<DataGridRow>().Class("alt"));
        altStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, AltBrush));

        var window = new Window { Width = 420, Height = 340, Content = grid };
        window.Styles.Add(altStyle);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid);
    }

    private static IEnumerable<DataGridRow> RealizedRows(DataGrid grid)
        => grid.GetVisualDescendants().OfType<DataGridRow>();

    private static DataGridRow RealizedRowFor(DataGrid grid, object item)
    {
        DataGridRow? row = RealizedRows(grid).FirstOrDefault(r => ReferenceEquals(r.DataContext, item));
        Assert.True(row is not null, "the DataGridRow did not realize headlessly");
        return row!;
    }

    private static void AssertEveryRealizedRowStripedByIndex(DataGrid grid)
    {
        foreach (DataGridRow row in RealizedRows(grid))
        {
            Assert.Equal(row.Index % 2 == 1, row.Classes.Contains("alt"));
        }
    }
}
