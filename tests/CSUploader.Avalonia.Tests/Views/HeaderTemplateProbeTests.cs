// <copyright file="HeaderTemplateProbeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.DevTools;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the custom <c>DataGridColumnHeader</c> re-template the
/// <see cref="HeaderTemplateProbeWindow"/> pins (Phase 6 Task 1, prep item 1) — all four GO-relevant
/// mechanisms driven with real synthesized input: the theme applies (realized headers carry the lock
/// <see cref="ToggleButton"/>), a drag on the header's right edge resizes the column AND the lock makes
/// that drag a no-op (<see cref="DataGridColumn.CanUserResize"/>), and a header-body click still raises
/// the DataGrid's sort. The lock-vs-body hit-test separation (checklist item 3) and the light/dark visual
/// parity are recorded in the bridge session, not here. When Task 10 deletes the probe, these retarget to
/// <c>UploadsViewTests</c> over the real view.
/// </summary>
public class HeaderTemplateProbeTests
{
    // ── Checklist 3/4 (theme applies): the custom ControlTheme reaches every realized header ──

    [AvaloniaFact]
    public void CustomHeaderTheme_Applies_EveryRealizedHeaderCarriesLockToggle()
    {
        var window = new HeaderTemplateProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var headers = window.ProbeGrid.GetVisualDescendants()
                .OfType<DataGridColumnHeader>()
                .Where(h => h.Content is not null)
                .ToList();

            // 4 data columns → 4 content headers realize (the DataGrid's filler header has null Content).
            Assert.Equal(4, headers.Count);
            Assert.All(headers, h => Assert.NotNull(LockToggleOf(h)));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 1 (mechanism half): the lock writes CanUserResize; unchecking restores it ──

    [AvaloniaFact]
    public void LockToggle_SetsColumnCanUserResizeFalse_UncheckRestores()
    {
        var window = new HeaderTemplateProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGridColumnHeader nameHeader = HeaderWithContent(window, "Name");
            DataGridColumn nameColumn = window.ProbeGrid.Columns[0];
            ToggleButton toggle = LockToggleOf(nameHeader)!;

            Assert.True(nameColumn.CanUserResize); // default (grid CanUserResizeColumns=true)

            // Lock ON: the user check flips IsChecked, then the handler runs. The drag-resize no-op that
            // this produces is the bridge's item 1; here we assert the state the resize logic reads.
            toggle.IsChecked = true;
            window.ApplyColumnLock(toggle);
            Assert.False(nameColumn.CanUserResize);

            // Lock OFF: restores resize — and it targeted only this column, not its neighbours.
            toggle.IsChecked = false;
            window.ApplyColumnLock(toggle);
            Assert.True(nameColumn.CanUserResize);
            Assert.All(window.ProbeGrid.Columns, c => Assert.True(c.CanUserResize));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 2 (FUNCTION is the GO gate): a header click still sorts through the custom template ──

    [AvaloniaFact]
    public void HeaderClick_OnCustomTemplate_StillSortsTheGrid()
    {
        var window = new HeaderTemplateProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGridColumnHeader nameHeader = HeaderWithContent(window, "Name");

            // Click the Name header's centre (the wide 2* column — well clear of the right-edge lock and
            // the 5px resize region). Sort is the header's OWN PointerReleased handler, so a real
            // down+up sequence exercises it exactly as a user click would.
            Point centre = CenterInWindow(nameHeader, window);
            window.MouseDown(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The header reflects the sort via its :sortascending pseudo-class (set by the same handler
            // after ProcessSort) …
            Assert.Contains(":sortascending", nameHeader.Classes);

            // … and the rows are now in ascending Name order (unsorted top row was fake_movie.mkv).
            var realizedNames = window.ProbeGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(r => r.DataContext is HeaderTemplateProbeWindow.ProbeRow)
                .OrderBy(r => r.Index)
                .Select(r => ((HeaderTemplateProbeWindow.ProbeRow)r.DataContext!).Name)
                .ToList();
            Assert.Equal("fake_archive.zip", realizedNames[0]);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 1 (FUNCTION, the GO gate): drag-resize still works, and the lock makes it a no-op ──

    [AvaloniaFact]
    public void DragResize_ChangesWidth_AndLockMakesItANoOp()
    {
        var window = new HeaderTemplateProbeWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGridColumnHeader nameHeader = HeaderWithContent(window, "Name");
            DataGridColumn nameColumn = window.ProbeGrid.Columns[0];
            ToggleButton toggle = LockToggleOf(nameHeader)!;

            double before = nameColumn.ActualWidth;

            // Drag the header's right edge (inside the 5px resize region) rightwards by 60px. This is a
            // REAL pointer drag through the custom template — no PART_*Gripper exists, so this proves the
            // header's own edge-resize logic survives the re-template.
            DragColumnEdge(window, nameHeader, +60);
            double afterDrag = nameColumn.ActualWidth;
            Assert.True(afterDrag > before + 20,
                $"drag-resize did not widen the column ({before} -> {afterDrag})");

            // Lock ON → the same edge drag is a no-op (CanResizeColumn reads ActualCanUserResize=false).
            toggle.IsChecked = true;
            window.ApplyColumnLock(toggle);
            double lockedBefore = nameColumn.ActualWidth;
            DragColumnEdge(window, nameHeader, +60);
            Assert.Equal(lockedBefore, nameColumn.ActualWidth, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    // Real pointer drag on the header's right edge: hover the edge, press, move by dx, release.
    private static void DragColumnEdge(HeaderTemplateProbeWindow window, DataGridColumnHeader header, double dx)
    {
        double y = header.Bounds.Height / 2;
        Point edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, y), window) ?? default;
        Point moved = new(edge.X + dx, edge.Y);
        window.MouseMove(edge);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(edge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(moved);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(moved, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridColumnHeader HeaderWithContent(HeaderTemplateProbeWindow window, string content)
        => window.ProbeGrid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .First(h => Equals(h.Content, content));

    private static ToggleButton? LockToggleOf(DataGridColumnHeader header)
        => header.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == "LockToggle");

    private static Point CenterInWindow(Visual v, Visual window)
        => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window) ?? default;
}
