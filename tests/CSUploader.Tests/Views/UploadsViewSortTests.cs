// <copyright file="UploadsViewSortTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Lib.Localization;
using CSUploader.Dal;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The Uploads tab's hierarchical sort, driven through real header clicks on the realized grid.
/// <para>
/// The ordering rules are pinned in the Core suite; what needs a realized grid is the wiring — that
/// a click reaches the ViewModel at all, that the grid's OWN sort stays suppressed (a stock sort
/// description would rank every row against every other and break the tree), that the indicator
/// lands on the right header, and that a template column like Hoster — inert before this change —
/// now sorts.
/// </para>
/// </summary>
public class UploadsViewSortTests
{
    [AvaloniaFact]
    public void HosterHeaderClick_NowSorts_AndKeepsFilesUnderTheirPackage()
    {
        // The column the whole change started from: Hoster is a DataGridTemplateColumn, and before
        // this its header did nothing at all.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Zulu pack", "z.bin");
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            ClickHeader(window, view, "Hoster");

            Assert.NotNull(harness.Vm.ActiveSort);
            Assert.Equal("HosterDisplay", harness.Vm.ActiveSort!.Path);
            AssertEveryFileSitsUnderItsOwnPackage(harness.Vm);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HeaderClick_SortsRowsHierarchically_AndTogglesDirectionOnTheSecondClick()
    {
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Zulu pack", "z.bin");
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            ClickHeader(window, view, "Name");
            Assert.Equal(
                ["Alpha pack", "a.bin", "Zulu pack", "z.bin"],
                harness.Vm.VisibleRows.Select(Display));

            ClickHeader(window, view, "Name");
            Assert.Equal(ListSortDirection.Descending, harness.Vm.ActiveSort!.Direction);
            Assert.Equal(
                ["Zulu pack", "z.bin", "Alpha pack", "a.bin"],
                harness.Vm.VisibleRows.Select(Display));

            // Descending must not float files above their own package row.
            AssertEveryFileSitsUnderItsOwnPackage(harness.Vm);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EveryColumnHeader_Sorts()
    {
        // The regression the review predicted: the fifteen bound columns sorted only because
        // Avalonia derived their path internally, and suppressing the stock sort would have taken
        // that away silently. Each realized header must now drive the VM.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            List<string> inert = [];
            foreach (DataGridColumnHeader header in RealizedHeaders(view))
            {
                harness.Vm.ApplySort(null);
                Click(window, header);
                if (harness.Vm.ActiveSort is null)
                {
                    inert.Add(header.Content?.ToString() ?? "(unnamed)");
                }
            }

            Assert.True(inert.Count == 0, "These headers did not sort: " + string.Join(", ", inert));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExpandingAfterSorting_PutsTheFilesBackUnderTheirPackage()
    {
        // The exact shape that killed the first design: a collapsed package's files are spliced
        // back in at a computed index, and Avalonia's own sorted insert placed one of them ABOVE
        // its package row.
        //
        // Seeded through the manager, NOT SeedPackage: the latter never subscribes the package's
        // PropertyChanged, so IsExpanded would move no rows and this test would pass while
        // exercising nothing. The collapse is asserted below for exactly that reason.
        using UploadsViewTests.VmHarness harness = new();
        Package zulu = await harness.AddPackageThroughManagerAsync("Zulu pack", "z-b.bin", "z-a.bin");
        await harness.AddPackageThroughManagerAsync("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            ClickHeader(window, view, "Name");

            zulu.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Alpha pack", "a.bin", "Zulu pack"], harness.Vm.VisibleRows.Select(Display));

            zulu.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            AssertEveryFileSitsUnderItsOwnPackage(harness.Vm);
            Assert.Equal(
                ["Alpha pack", "a.bin", "Zulu pack", "z-a.bin", "z-b.bin"],
                harness.Vm.VisibleRows.Select(Display));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EveryColumn_IncludingHiddenOnes_CanBeSorted()
    {
        // CanUserSort is checked before Sorting is ever raised, and Avalonia infers it by looking
        // for the sort path on the collection view's item type — which resolves to Package, the
        // first row. QueueOrder is the one path packages lack, so the Order header was inert while
        // every other column worked. A visible-headers-only sweep cannot see this: Order ships
        // hidden.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            string[] inert =
            [
                .. view.uploadsGrid.Columns
                    .Where(c => !c.CanUserSort)
                    .Select(c => c.SortMemberPath ?? "(no path)"),
            ];

            Assert.True(inert.Length == 0, "These columns cannot sort at all: " + string.Join(", ", inert));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SortingWhileFiltering_KeepsFilesUnderTheirPackage()
    {
        // A granular insert into a FILTERED collection view lands at the raw source index clamped
        // to the filtered length — Avalonia never translates source index to filtered index — so a
        // package arriving mid-list while filtered was placed wrongly, and could be separated from
        // its own file by an unrelated row.
        using UploadsViewTests.VmHarness harness = new();
        await harness.AddPackageThroughManagerAsync("x-alpha", "x-a.bin");
        await harness.AddPackageThroughManagerAsync("hidden one", "nomatch.bin");
        await harness.AddPackageThroughManagerAsync("x-zulu", "x-z.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            ClickHeader(window, view, "Name");
            harness.Vm.FilterText = "x-";
            Dispatcher.UIThread.RunJobs();

            // Arrives mid-rank while the filter is on.
            await harness.AddPackageThroughManagerAsync("x-mike", "x-m.bin");
            Dispatcher.UIThread.RunJobs();

            AssertEveryFileSitsUnderItsOwnPackage(harness.Vm);
            Assert.Equal(
                ["x-alpha", "x-a.bin", "x-mike", "x-m.bin", "x-zulu", "x-z.bin"],
                view.RowsView!.Cast<object>().Select(Display));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SortIndicator_MarksOnlyTheSortedHeader_AndClearsWhenTheSortGoes()
    {
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            ClickHeader(window, view, "Name");

            Assert.Contains("sorted-asc", HeaderFor(view, "Name").Classes);
            Assert.All(
                RealizedHeaders(view).Where(h => !Equals(h.Content, HeaderFor(view, "Name").Content)),
                h =>
                {
                    Assert.DoesNotContain("sorted-asc", h.Classes);
                    Assert.DoesNotContain("sorted-desc", h.Classes);
                });

            ClickHeader(window, view, "Name");
            Assert.Contains("sorted-desc", HeaderFor(view, "Name").Classes);
            Assert.DoesNotContain("sorted-asc", HeaderFor(view, "Name").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SortedGrid_InstallsNoSortDescription()
    {
        // Belt to the retargeted probe's braces: the grid's own sort must stay out of it entirely,
        // on every column, because its insert placement is what broke the tree.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            foreach (DataGridColumnHeader header in RealizedHeaders(view))
            {
                Click(window, header);
                Assert.Empty(view.RowsView!.SortDescriptions);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PersistedSort_IsRestoredOnLoad_WithItsIndicator()
    {
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Zulu pack", "z.bin");
        harness.SeedPackage("Alpha pack", "a.bin");
        await harness.Vm.SettingRepo!.UpsertAsync(
            SettingKey.UploadsTabSort, new UploadSort("Name", ListSortDirection.Descending).Format());

        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new UploadSort("Name", ListSortDirection.Descending), harness.Vm.ActiveSort);
            Assert.Equal(
                ["Zulu pack", "z.bin", "Alpha pack", "a.bin"],
                harness.Vm.VisibleRows.Select(Display));
            Assert.Contains("sorted-desc", HeaderFor(view, "Name").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PersistedSort_NamingAColumnThatIsHidden_IsNotApplied()
    {
        // An active sort whose header is not on screen is a grid the user can neither explain nor
        // undo, so a hidden column's persisted sort is dropped rather than applied invisibly.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Zulu pack", "z.bin");
        harness.SeedPackage("Alpha pack", "a.bin");
        await harness.Vm.SettingRepo!.UpsertAsync(
            SettingKey.UploadsTabSort, new UploadSort("FileHash", ListSortDirection.Ascending).Format());

        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Hash ships hidden by default.
            Assert.False(view.uploadsGrid.Columns.Single(c => c.SortMemberPath == "FileHash").IsVisible);
            Assert.Null(harness.Vm.ActiveSort);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DataContextSwap_StopsListeningToTheOldViewModel()
    {
        // Regression: the cleared-sort subscription was originally wired in OnGridLoaded, whose
        // _columnsWired guard runs once per CONTROL, not once per VM. That left the first VM
        // subscribed for the control's lifetime — the leak class this view already guards its
        // collection view against — and meant a detached VM could still reach in and wipe the
        // live one's indicator.
        using UploadsViewTests.VmHarness first = new();
        using UploadsViewTests.VmHarness second = new();
        first.SeedPackage("Old pack", "old.bin");
        second.SeedPackage("New pack", "new.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(first.Vm);
        try
        {
            view.DataContext = second.Vm;
            Dispatcher.UIThread.RunJobs();
            ClickHeader(window, view, "Name");
            Assert.Contains("sorted-asc", HeaderFor(view, "Name").Classes);

            // The detached VM clears its own sort. The view must not hear it.
            first.Vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));
            first.Vm.SelectedRows = [first.Vm.Packages.Single().First()];
            first.Vm.MoveSelectedCommand.Execute("-1");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(first.Vm.ActiveSort);
            Assert.NotNull(second.Vm.ActiveSort);
            Assert.Contains("sorted-asc", HeaderFor(view, "Name").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SortIndicator_ActuallyRendersTheGlyph()
    {
        // Asserting the CLASS alone would pass even if the header theme's selector no longer
        // matched it — and the selectors had to change (from the stock :sortascending pseudo-class,
        // which the DataGrid clears out from under us, to our own class). So check the effect the
        // style is supposed to have: the glyph Path visible, carrying the right geometry.
        using UploadsViewTests.VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "a.bin");
        (Window window, UploadsView view) = UploadsViewTests.Show(harness.Vm);
        try
        {
            Shapes.Path glyph = GlyphOf(HeaderFor(view, "Name"));
            Assert.False(glyph.IsVisible);

            ClickHeader(window, view, "Name");
            Dispatcher.UIThread.RunJobs();
            Assert.True(glyph.IsVisible, "the ascending sort glyph never became visible");
            object? ascending = view.FindResource("UploadsSortIconAscendingPath");
            Assert.Equal(ascending, glyph.Data);

            ClickHeader(window, view, "Name");
            Dispatcher.UIThread.RunJobs();
            Assert.True(glyph.IsVisible);
            Assert.Equal(view.FindResource("UploadsSortIconDescendingPath"), glyph.Data);
            Assert.NotEqual(ascending, glyph.Data);
        }
        finally
        {
            window.Close();
        }
    }

    private static Shapes.Path GlyphOf(DataGridColumnHeader header)
        => header.GetVisualDescendants().OfType<Shapes.Path>().Single(p => p.Name == "SortIcon");

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static string Display(object row) => row switch
    {
        Package package => package.Name,
        PackageFile file => file.Name,
        _ => row.ToString() ?? "?",
    };

    private static IEnumerable<DataGridColumnHeader> RealizedHeaders(UploadsView view)
        => [.. view.uploadsGrid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Where(h => h.Content is not null && h.IsVisible)];

    private static DataGridColumnHeader HeaderFor(UploadsView view, string columnKey)
    {
        string header = Localizer.Instance["Uploads_Col_" + columnKey];
        return RealizedHeaders(view).First(h => Equals(h.Content, header));
    }

    private static void ClickHeader(Window window, UploadsView view, string columnKey)
        => Click(window, HeaderFor(view, columnKey));

    private static void Click(Window window, DataGridColumnHeader header)
    {
        Point centre = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)
            ?? new Point(0, 0);
        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertEveryFileSitsUnderItsOwnPackage(UploadsViewModel vm)
    {
        Package? current = null;
        foreach (object row in vm.VisibleRows)
        {
            if (row is Package package)
            {
                current = package;
                continue;
            }

            PackageFile file = Assert.IsType<PackageFile>(row);
            Assert.Same(current, file.Package);
        }
    }
}
