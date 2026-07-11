// <copyright file="GroupingProbeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.DevTools;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the DataGrid grouping recipe the <see cref="GroupingProbeWindow"/> pins
/// (Phase 5 Task 2, prep item 6). These are the RUNTIME behaviors the go/no-go turns on — the API
/// surface (DataGridCollectionView / DataGridPathGroupDescription / CollapseRowGroup) was confirmed
/// against the installed 11.3.13 at plan time. When Task 5 deletes the probe, these retarget to
/// UploadedViewTests over the real VM collection.
/// </summary>
public class GroupingProbeTests
{
    // ── Checklist 1: the view groups correctly (built in head code-behind) ──

    [AvaloniaFact]
    public void BuildView_GroupsByPackageName_ThreeGroupsWithMatchingKeysAndCounts()
    {
        DataGridCollectionView view = GroupingProbeWindow.BuildView();

        Assert.Equal(3, view.Groups.Count);

        var groups = view.Groups.Cast<DataGridCollectionViewGroup>().ToList();
        Assert.Equal(
            new[] { "Fake pack (photos)", "Fake pack (documents)", "Fake pack (archive set)" },
            groups.Select(g => (string)g.Key));
        // Uneven sizes are the point — the probe must exercise multi-size groups.
        Assert.Equal(new[] { 3, 2, 2 }, groups.Select(g => g.ItemCount));
    }

    // ── Checklist 3: collapse hides the group's rows; re-expand restores them ──

    [AvaloniaFact]
    public void CollapseRowGroup_HidesGroupRows_ExpandRestores()
    {
        var window = new GroupingProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.ProbeGrid;
            var view = (DataGridCollectionView)grid.ItemsSource!;
            var firstGroup = (DataGridCollectionViewGroup)view.Groups[0]; // "photos" — 3 rows

            int realizedAllExpanded = RealizedRowCount(grid);
            Assert.Equal(7, realizedAllExpanded); // all 7 file rows realize in a 900x500 window

            grid.CollapseRowGroup(firstGroup, collapseAllSubgroups: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, RealizedRowCount(grid)); // photos' 3 rows are gone (2 + 2 remain)

            grid.ExpandRowGroup(firstGroup, expandAllSubgroups: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(7, RealizedRowCount(grid)); // restored
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 4: built-in Ctrl+C on a grouped view (Reality-check #3) ──

    [AvaloniaFact]
    public async Task CtrlC_OnGroupedView_CopiesHeaderPlusSelectedRows_NoGroupHeaderPollution()
    {
        var window = new GroupingProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.ProbeGrid;

            // Select two file rows spanning the first group (the actual row items, not group headers).
            grid.SelectedItems.Add(GroupingProbeWindow.Rows[0]); // photos / fake_beach.jpg
            grid.SelectedItems.Add(GroupingProbeWindow.Rows[1]); // photos / fake_sunset.png
            Dispatcher.UIThread.RunJobs();

            // Synthetic Ctrl+C raised on the grid — does it reach the DataGrid's internal clipboard
            // path? (The whole point of Reality-check #3.) ProcessCopyKey builds header + tab-separated
            // rows from each column's ClipboardContentBinding and writes via TopLevel.Clipboard.
            grid.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.C,
                KeyModifiers = KeyModifiers.Control,
            });
            Dispatcher.UIThread.RunJobs();

            string? clip = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
            Assert.NotNull(clip);
            // Avalonia's FormatClipboardContent quotes every cell and tab-separates them; IncludeHeader
            // prepends the column-header row. So the header line is "Name"\t"Size"\t"URL".
            Assert.Contains("\"Name\"\t\"Size\"\t\"URL\"", clip, StringComparison.Ordinal);
            Assert.Contains("\"fake_beach.jpg\"", clip!, StringComparison.Ordinal);
            Assert.Contains("\"fake_sunset.png\"", clip!, StringComparison.Ordinal);
            // The copy iterates SelectedItems (data rows) only — group headers must NOT pollute it.
            Assert.DoesNotContain("Fake pack", clip!, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 7: the zebra-striping index basis under grouping (Reality-check #8) ──

    [AvaloniaFact]
    public void GetIndex_NumbersFileRowsFlatAcrossGroups()
    {
        var window = new GroupingProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.ProbeGrid;

            // Map each realized row's Index to its file name, in Rows order. If Index counted slots
            // (group headers included), the values would be 1,2,3,5,6,8,9; a flat file-item index is
            // 0..6 — the basis the shared zebra helper (Task 4) alternates on. NB: DataGridRow.GetIndex()
            // is OBSOLETE in 11.3.13 ("Use the Index property instead") — the finding Task 4 must heed,
            // since the plan's zebra-helper snippet still calls GetIndex().
            var byItem = grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(r => r.DataContext is GroupingProbeWindow.ProbeRow)
                .ToDictionary(r => ((GroupingProbeWindow.ProbeRow)r.DataContext!).FileName, r => r.Index);

            Assert.Equal(7, byItem.Count);
            Assert.Equal(0, byItem["fake_beach.jpg"]);   // group 0, row 0
            Assert.Equal(2, byItem["fake_pano.raw"]);    // group 0, row 2
            Assert.Equal(3, byItem["fake_report.pdf"]);  // group 1, row 0 → flat index 3 (no header gap)
            Assert.Equal(6, byItem["fake_part2.rar"]);   // group 2, row 1 → flat index 6
        }
        finally
        {
            window.Close();
        }
    }

    private static int RealizedRowCount(DataGrid grid)
        => grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);
}
