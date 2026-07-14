// <copyright file="UploadsViewTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Update;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static CSUploader.Tests.Avalonia.LeakProbes;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="UploadsView"/> (Phase 6 Task 10): the custom
/// <c>DataGridColumnHeader</c> re-template (the retargeted Task 1 probe asserts — theme applies, lock writes
/// <see cref="DataGridColumn.CanUserResize"/>, drag-resize + sort survive), the durable filtered collection
/// view (built once, no subscriber leak across refreshes), the live <c>FilterText</c> filter, the expand
/// chevron, the progress-cell MultiBinding, the rule-32 read-only columns, the column menu, and zebra
/// striping. Every shown window is closed in a <c>finally</c> (headless windows are process-global for the
/// session).
/// </summary>
public class UploadsViewTests
{
    // ── Retargeted Task 1 probe: the custom header theme reaches every realized (visible) header ──

    [AvaloniaFact]
    public void CustomHeaderTheme_Applies_EveryVisibleHeaderCarriesLockToggle()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            var headers = view.uploadsGrid.GetVisualDescendants()
                .OfType<DataGridColumnHeader>()
                .Where(h => h.Content is not null && h.IsVisible)
                .ToList();

            Assert.NotEmpty(headers);
            Assert.All(headers, h => Assert.NotNull(LockToggleOf(h)));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Retargeted Task 1 probe (mechanism): the lock writes CanUserResize; unchecking restores it ──

    [AvaloniaFact]
    public void LockToggle_SetsColumnCanUserResizeFalse_UncheckRestores()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridColumnHeader nameHeader = HeaderWithContent(view, "Name");
            DataGridColumn nameColumn = view.uploadsGrid.Columns[0];
            ToggleButton toggle = LockToggleOf(nameHeader)!;

            Assert.True(nameColumn.CanUserResize);

            toggle.IsChecked = true;
            view.ApplyColumnLock(toggle);
            Assert.False(nameColumn.CanUserResize);

            toggle.IsChecked = false;
            view.ApplyColumnLock(toggle);
            Assert.True(nameColumn.CanUserResize);

            // The lock targeted only its own column, not its neighbours.
            Assert.All(view.uploadsGrid.Columns, c => Assert.True(c.CanUserResize));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Retargeted Task 1 probe (GO gate): a header click still sorts through the custom template ──

    [AvaloniaFact]
    public void HeaderClick_OnCustomTemplate_StillSortsTheGrid()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            // Account is a DataGridTextColumn (its Binding is the sort path); template columns without a
            // SortMemberPath don't sort, matching the WPF, so a bound text header is the sort probe.
            DataGridColumnHeader accountHeader = HeaderWithContent(view, "Account");

            Point centre = CenterInWindow(accountHeader, window);
            window.MouseDown(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The header reflects the sort via its :sortascending pseudo-class (set after ProcessSort) and
            // the view gained a sort description — the sort FUNCTION fired through the re-templated header.
            Assert.Contains(":sortascending", accountHeader.Classes);
            Assert.NotEmpty(view.RowsView!.SortDescriptions);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Retargeted Task 1 probe (GO gate): drag-resize still works, and the lock makes it a no-op ──

    [AvaloniaFact]
    public void DragResize_ChangesWidth_AndLockMakesItANoOp()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridColumnHeader nameHeader = HeaderWithContent(view, "Name");
            DataGridColumn nameColumn = view.uploadsGrid.Columns[0];
            ToggleButton toggle = LockToggleOf(nameHeader)!;

            double before = nameColumn.ActualWidth;

            DragColumnEdge(window, nameHeader, +60);
            double afterDrag = nameColumn.ActualWidth;
            Assert.True(afterDrag > before + 20, $"drag-resize did not widen the column ({before} -> {afterDrag})");

            toggle.IsChecked = true;
            view.ApplyColumnLock(toggle);
            double lockedBefore = nameColumn.ActualWidth;
            DragColumnEdge(window, nameHeader, +60);
            Assert.Equal(lockedBefore, nameColumn.ActualWidth, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Durable filtered view: built once and kept; refreshes never re-mint it or leak a subscriber ──

    [AvaloniaFact]
    public void FilterInvalidated_RefreshesInPlace_NoViewRemint_NoSubscriberLeak()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            object viewBefore = view.uploadsGrid.ItemsSource!;
            Assert.IsType<DataGridCollectionView>(viewBefore);
            int baseline = CollectionChangedSubscriberCount(harness.Vm.VisibleRows);

            // Each FilterText edit raises FilterInvalidated -> the view Refreshes IN PLACE. The durable view
            // must not be re-minted (which would orphan a subscriber on the long-lived VisibleRows — the
            // Phase 5 leak class), so both the subscriber count and the ItemsSource instance stay flat.
            for (int i = 0; i < 5; i++)
            {
                harness.Vm.FilterText = $"needle{i}";
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(baseline, CollectionChangedSubscriberCount(harness.Vm.VisibleRows));
            Assert.Same(viewBefore, view.uploadsGrid.ItemsSource);
        }
        finally
        {
            window.Close();
        }
    }

    // ── FilterText filters the flat VisibleRows through the head's view.Filter = vm.MatchesFilter ──

    [AvaloniaFact]
    public void FilterText_FiltersVisibleRows_ThroughMatchesFilter()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        harness.SeedPackage("Beta pack", "beta1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            var rowsView = (DataGridCollectionView)view.uploadsGrid.ItemsSource!;

            // No filter: 2 packages + 2 (expanded) file rows.
            Assert.Equal(4, CountOf(rowsView));

            // "alpha1" matches only the file row alpha1.bin (the package name "Alpha pack" lacks it).
            harness.Vm.FilterText = "alpha1";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, CountOf(rowsView));

            // "pack" matches both package names, neither file name.
            harness.Vm.FilterText = "pack";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, CountOf(rowsView));

            // Clearing restores every row.
            harness.Vm.FilterText = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, CountOf(rowsView));
        }
        finally
        {
            window.Close();
        }
    }

    // ── The Name-column chevron toggles the package's IsExpanded ──

    [AvaloniaFact]
    public void ExpandChevron_TogglesPackageIsExpanded()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridRow packageRow = RowFor(view.uploadsGrid, package);
            ToggleButton chevron = packageRow.GetVisualDescendants()
                .OfType<ToggleButton>()
                .First(t => t.Classes.Contains("name-chevron"));

            Assert.True(package.IsExpanded); // packages default to expanded

            chevron.IsChecked = false;
            RaiseClick(chevron);
            Assert.False(package.IsExpanded);

            chevron.IsChecked = true;
            RaiseClick(chevron);
            Assert.True(package.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Progress cell: the fill Border width is the MultiBinding of Progress × the named grid's width ──

    [AvaloniaFact]
    public void ProgressCell_FillWidthTracksProgress_ViaMultiBinding()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single();
        file.Progress = 40.0;

        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridRow fileRow = RowFor(view.uploadsGrid, file);
            Grid pgrid = fileRow.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PGrid");
            Border fill = pgrid.Children.OfType<Border>().First(b => b.HorizontalAlignment == HorizontalAlignment.Left);

            Assert.True(pgrid.Bounds.Width > 0);
            double expected = pgrid.Bounds.Width * 0.40;
            Assert.True(fill.Width > 0, "progress fill width did not resolve (> 0)");
            Assert.True(Math.Abs(fill.Width - expected) < 1.0, $"expected ~{expected}, got {fill.Width}");
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 32: the read-only converter text columns must not write back through their (throwing) converters ──

    [AvaloniaFact]
    public void ReadOnlyConverterColumns_DoNotWriteBack_SourceValuesSurviveBinding()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single();
        file.Speed = 12_345;
        file.BytesLoaded = 5_000;

        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            // Avalonia's DataGridTextColumn.Binding defaults to TwoWay and pushes the ConvertBack result to
            // the source on bind — even in a read-only cell. ByteUnitConverter throws on ConvertBack, so
            // without Mode=OneWay the bind would blank Speed / BytesLoaded. Assert they survived.
            Assert.Equal(12_345, file.Speed);
            Assert.Equal(5_000, file.BytesLoaded);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Column menu attached, with the first (anchor Name) column toggle disabled ──

    [AvaloniaFact]
    public void ColumnMenu_AttachedWithFirstItemDisabled()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            PumpUntil(() => view.ColumnMenu is not null);

            Assert.NotNull(view.ColumnMenu);
            var first = Assert.IsType<MenuItem>(view.ColumnMenu!.Items[0]);
            Assert.False(first.IsEnabled); // Name stays visible — it anchors the expand chevron
        }
        finally
        {
            window.Close();
        }
    }

    // ── Zebra: alternating rows carry the .alt class (index-parity basis) ──

    [AvaloniaFact]
    public void Zebra_AlternatingRowsCarryAltClass()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin", "alpha2.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            var rows = view.uploadsGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(r => r.DataContext is Package or PackageFile)
                .OrderBy(r => r.Index)
                .ToList();

            Assert.True(rows.Count >= 3); // package + 2 files
            Assert.All(rows, r => Assert.Equal(r.Index % 2 == 1, r.Classes.Contains("alt")));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Editable Order (prep 7): BeginningEdit guard allows Order on a non-terminal file, cancels otherwise ──

    [AvaloniaFact]
    public void BeginningEdit_AllowsOrderOnNonTerminalFile_CancelsEverythingElse()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single();
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridColumn orderColumn = OrderColumnOf(view);
            DataGridColumn nameColumn = view.uploadsGrid.Columns[0];

            // Order cell on a non-terminal (Idle) file → editable (not cancelled).
            Assert.False(view.ShouldCancelEdit(orderColumn, file));

            // Any other column → cancelled (the grid is editable ONLY for the Order cell).
            Assert.True(view.ShouldCancelEdit(nameColumn, file));

            // Package row → cancelled (packages show a blank order).
            Assert.True(view.ShouldCancelEdit(orderColumn, package));

            // Terminal file → cancelled (blank order once Completed/Failed/Cancelled).
            file.State = FileState.Completed;
            Assert.True(view.ShouldCancelEdit(orderColumn, file));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Editable Order: CellEditEnding on a valid int resolves the SetOrderCommand (file, target) tuple ──

    [AvaloniaFact]
    public void CellEditEnding_ValidInt_ResolvesFileTargetTuple_NullOtherwise()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single();
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridColumn orderColumn = OrderColumnOf(view);
            DataGridColumn nameColumn = view.uploadsGrid.Columns[0];

            (PackageFile File, int Target)? committed = view.ResolveOrderEdit(orderColumn, file, new TextBox { Text = "3" });
            Assert.NotNull(committed);
            Assert.Same(file, committed!.Value.File);
            Assert.Equal(3, committed.Value.Target);

            // No move: a non-Order column, a package row, a non-int, and a non-TextBox editing element.
            Assert.Null(view.ResolveOrderEdit(nameColumn, file, new TextBox { Text = "3" }));
            Assert.Null(view.ResolveOrderEdit(orderColumn, package, new TextBox { Text = "3" }));
            Assert.Null(view.ResolveOrderEdit(orderColumn, file, new TextBox { Text = "abc" }));
            Assert.Null(view.ResolveOrderEdit(orderColumn, file, new Button()));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Package-expanding copy: a selected Package copies with its child files (the divergence from UploadedView) ──

    [AvaloniaFact]
    public void RowCopy_ExpandsSelectedPackageToChildren_InTsv()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin", "alpha2.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            view.uploadsGrid.SelectedItems.Clear();
            view.uploadsGrid.SelectedItems.Add(package); // select ONLY the package row
            Dispatcher.UIThread.RunJobs();

            string? tsv = view.BuildRowCopyText();
            Assert.NotNull(tsv);

            // The package row is present AND both child files were expanded in AFTER it — the built-in copy
            // would have serialized only the single selected package. This also confirms §Reality-check #8:
            // the throwaway-bound-TextBlock evaluation of each column's ClipboardContentBinding resolves
            // synchronously (the names came from {Binding Name}); the ColumnValueExtractor fallback is unneeded.
            int pkgIdx = tsv!.IndexOf("Alpha pack", StringComparison.Ordinal);
            int child1 = tsv.IndexOf("alpha1.bin", StringComparison.Ordinal);
            int child2 = tsv.IndexOf("alpha2.bin", StringComparison.Ordinal);
            Assert.True(pkgIdx >= 0, "package row missing from the TSV");
            Assert.True(child1 > pkgIdx, "child alpha1.bin missing or not after the package row");
            Assert.True(child2 > pkgIdx, "child alpha2.bin missing or not after the package row");

            // IncludeHeader mode: the header row carries the localized column headers.
            Assert.Contains(Localizer.Instance["Uploads_Col_Name"], tsv, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Per-column Copy submenu: each of the 21 items carries its column key and routes CopyColumnCommand ──

    [AvaloniaFact]
    public void CopyColumnMenuItems_CarryColumnKeys_AndRouteCopyColumnCommand()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            ContextMenu menu = view.uploadsGrid.ContextMenu!;
            MenuItem copySubmenu = menu.Items.OfType<MenuItem>()
                .First(mi => Equals(mi.Header, Localizer.Instance["Common_Context_Copy"]));

            // The 21 per-column items carry their Uploads_Col_* suffix as CommandParameter (the row-copy item
            // has a Click handler + no CommandParameter, so it filters out).
            string[] keys = [.. copySubmenu.Items.OfType<MenuItem>()
                .Select(mi => mi.CommandParameter as string)
                .Where(p => p is not null)
                .Cast<string>()];
            string[] expected =
            [
                "Name", "Size", "Hoster", "Account", "Status", "Speed", "ETA", "BytesLoaded", "BytesRemaining",
                "Progress", "Path", "Added", "Finished", "Started", "ScheduledAt", "Duration", "Order",
                "SpeedLimit", "Hash", "URL", "Error",
            ];
            Assert.Equal(expected, keys);

            // Resolve the inherited VM DataContext down the menu's logical tree, then assert the "Name" item's
            // Command binding lands on CopyColumnCommand (a mistyped {Binding} would silently no-op otherwise).
            menu.DataContext = harness.Vm;
            Dispatcher.UIThread.RunJobs();
            MenuItem nameItem = copySubmenu.Items.OfType<MenuItem>().First(mi => (string?)mi.CommandParameter == "Name");
            Assert.Same(harness.Vm.CopyColumnCommand, nameItem.Command);
        }
        finally
        {
            window.Close();
        }
    }

    // ── ContextMenu.Opening: suppress on empty space, snapshot the multi-selection + show on a row (prep 8) ──

    [AvaloniaFact]
    public void Opening_SuppressesOnWhitespace_SnapshotsSelection_ShowsOnRow()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single();
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            // Empty-space right-click (source is the grid, not a row) → suppress the menu.
            view.ApplyRightClickSelection(view.uploadsGrid);
            Assert.True(view.SnapshotSelectionAndDecideSuppression());

            // Right-click a row → do not suppress, and the VM's SelectedRows snapshot is taken.
            view.uploadsGrid.SelectedItems.Clear();
            view.uploadsGrid.SelectedItems.Add(file);
            view.ApplyRightClickSelection(RowFor(view.uploadsGrid, file));
            Assert.False(view.SnapshotSelectionAndDecideSuppression());
            Assert.Single(harness.Vm.SelectedRows);
            Assert.Same(file, harness.Vm.SelectedRows[0]);
        }
        finally
        {
            window.Close();
        }
    }

    // ── SelectRowOnRightClick FIRST consumer (prep 8): a real right-press selects an unselected row ──

    [AvaloniaFact]
    public void RightClick_OnUnselectedRow_SelectsIt_ViaBehavior()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        Package beta = harness.SeedPackage("Beta pack", "beta1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGridRow betaRow = RowFor(view.uploadsGrid, beta);
            window.MouseDown(CenterInWindow(betaRow, window), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view.uploadsGrid.SelectedItems);
            Assert.Same(beta, view.uploadsGrid.SelectedItems[0]);
        }
        finally
        {
            window.Close();
        }
    }

    // ── SelectRowOnRightClick: a right-press INSIDE a multi-selection preserves it (Explorer UX, prep 8) ──

    [AvaloniaFact]
    public void RightClick_InsideMultiSelection_PreservesIt_ViaBehavior()
    {
        using VmHarness harness = new();
        Package alpha = harness.SeedPackage("Alpha pack", "alpha1.bin");
        Package beta = harness.SeedPackage("Beta pack", "beta1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            view.uploadsGrid.SelectedItems.Add(alpha);
            view.uploadsGrid.SelectedItems.Add(beta);
            Dispatcher.UIThread.RunJobs();

            DataGridRow alphaRow = RowFor(view.uploadsGrid, alpha);
            window.MouseDown(CenterInWindow(alphaRow, window), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, view.uploadsGrid.SelectedItems.Count);
        }
        finally
        {
            window.Close();
        }
    }

    // ── State-appropriate menu items: Start/ForceStart/Stop visibility tracks the SelectedRow's state ──
    // (The bridge cannot open this menu — ava_input rightclick raises ContextRequested without a real pointer
    //  press, so _rightClickOnItem stays false and the whitespace-suppression correctly cancels it — so this
    //  head-side test stands in for the "menu opens with state-appropriate items" bridge check.)

    [AvaloniaFact]
    public void ContextMenu_StartForceStartStop_VisibilityTracksSelectedRowState()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin", "alpha2.bin");
        PackageFile idle = package.First();      // default Idle state
        PackageFile queued = package.Last();
        queued.State = FileState.UploadQueued;

        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            ContextMenu menu = view.uploadsGrid.ContextMenu!;
            menu.DataContext = harness.Vm; // resolve the inherited VM binding for the (closed) menu

            // Idle file: Start + ForceStart visible; Stop hidden (nothing to stop, it isn't in the pipeline).
            harness.Vm.SelectedRow = idle;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.StartMenuItem.IsVisible);
            Assert.True(view.ForceStartMenuItem.IsVisible);
            Assert.False(view.StopMenuItem.IsVisible);

            // UploadQueued file: Start hidden (already queued); ForceStart visible (jump the limit); Stop visible.
            harness.Vm.SelectedRow = queued;
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.StartMenuItem.IsVisible);
            Assert.True(view.ForceStartMenuItem.IsVisible);
            Assert.True(view.StopMenuItem.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Delete key binding wired to RemoveSelectedCommand with the live SelectedItems parameter (rule 24) ──
    // (FIX 1: this EDITABLE grid's binding is the editor-guard wrapper, which delegates to RemoveSelectedCommand.)

    [AvaloniaFact]
    public void DeleteKeyBinding_WiredToRemoveSelectedCommand_WithSelectedItemsParameter()
    {
        using VmHarness harness = new();
        UploadsView view = new() { DataContext = harness.Vm };

        KeyBinding binding = Assert.Single(view.uploadsGrid.KeyBindings);
        Assert.Equal(Key.Delete, binding.Gesture.Key);
        var guarded = Assert.IsType<DataGridDeleteKeyGuard.EditorGuardedCommand>(binding.Command);
        Assert.Same(harness.Vm.RemoveSelectedCommand, guarded.Inner);
        Assert.Same(view.uploadsGrid.SelectedItems, binding.CommandParameter);
    }

    // ── FIX 1 (MAJOR twin): while the Order cell editor is focused, Delete edits text and does NOT remove ──

    [AvaloniaFact]
    public void DeleteKey_WhileOrderCellEditorFocused_EditsText_DoesNotRemove()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        PackageFile file = package.Single(); // default (non-terminal) state → the Order cell is editable
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.uploadsGrid;
            DataGridColumn orderColumn = OrderColumnOf(view);
            orderColumn.IsVisible = true; // the Order column ships hidden (the column menu toggles it)
            Dispatcher.UIThread.RunJobs();

            grid.SelectedItem = file;
            grid.ScrollIntoView(file, orderColumn);
            grid.CurrentColumn = orderColumn;
            Dispatcher.UIThread.RunJobs();

            Assert.True(grid.BeginEdit(), "BeginEdit should enter edit mode on the Order cell");
            Dispatcher.UIThread.RunJobs();

            // The only TextBox realized inside the grid is the Order CellEditingTemplate editor.
            TextBox editor = grid.GetVisualDescendants().OfType<TextBox>().First();
            editor.Text = "12";
            editor.CaretIndex = 0;
            editor.Focus();
            Dispatcher.UIThread.RunJobs();

            // The guard sees the focused cell editor → CanExecute false, so KeyBinding.TryHandle declines
            // WITHOUT marking the KeyDown Handled and the keystroke falls through to the editing TextBox.
            var guarded = (DataGridDeleteKeyGuard.EditorGuardedCommand)Assert.Single(grid.KeyBindings).Command;
            Assert.True(guarded.IsCellEditorFocused());
            Assert.False(guarded.CanExecute(grid.SelectedItems));

            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("2", editor.Text); // forward-delete removed the char at the caret (WPF parity)
            harness.DialogMock.Verify(
                d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            window.Close();
        }
    }

    // ── FIX 1: with NO cell editor focused, Delete still fires the remove path ──

    [AvaloniaFact]
    public void DeleteKey_WithNoEditorFocused_FiresRemovePath()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.uploadsGrid;
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(package);
            Dispatcher.UIThread.RunJobs();

            var guarded = (DataGridDeleteKeyGuard.EditorGuardedCommand)Assert.Single(grid.KeyBindings).Command;

            // No cell editor is focused → the guard delegates straight to RemoveSelectedCommand.
            Assert.False(guarded.IsCellEditorFocused());
            Assert.True(guarded.CanExecute(grid.SelectedItems));

            // Executing it runs the remove path — the opt-out confirmation is shown for the selected package
            // (the mock returns false, so nothing is actually removed, but the remove command fired).
            guarded.Execute(grid.SelectedItems);
            Dispatcher.UIThread.RunJobs();

            harness.DialogMock.Verify(
                d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Once);
        }
        finally
        {
            window.Close();
        }
    }

    // ── FIX 2: Ctrl+Insert (not just Ctrl+C) runs the package-expanding copy and is Handled (pin both gestures) ──

    [AvaloniaFact]
    public void CtrlInsertAndCtrlC_BothRunExpandingCopy_AndAreHandled()
    {
        using VmHarness harness = new();
        Package package = harness.SeedPackage("Alpha pack", "alpha1.bin", "alpha2.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            view.uploadsGrid.SelectedItems.Clear();
            view.uploadsGrid.SelectedItems.Add(package);
            Dispatcher.UIThread.RunJobs();

            IClipboard clipboard = TopLevel.GetTopLevel(view.uploadsGrid)!.Clipboard!;

            // Ctrl+Insert routes to the built-in DataGrid's flat ProcessCopyKey in stock Avalonia; the tunnel
            // intercept must claim it (Handled) and run the SAME expanding copy as Ctrl+C.
            Assert.True(RaiseCopyGesture(view, Key.Insert));
            string insertTsv = ReadClipboard(clipboard);
            Assert.Contains("Alpha pack", insertTsv, StringComparison.Ordinal);
            Assert.Contains("alpha1.bin", insertTsv, StringComparison.Ordinal);
            Assert.Contains("alpha2.bin", insertTsv, StringComparison.Ordinal); // child expansion — a flat copy would omit these

            clipboard.ClearAsync();
            Dispatcher.UIThread.RunJobs();

            // Ctrl+C still runs the same expanding copy (the other gesture stays intercepted too).
            Assert.True(RaiseCopyGesture(view, Key.C));
            Assert.Contains("alpha2.bin", ReadClipboard(clipboard), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Overview panel (Task 12): ShowUploadOverview toggles the whole panel's visibility (rule 33) ──

    [AvaloniaFact]
    public void ShowUploadOverview_TogglesOverviewPanelVisibility()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            Assert.True(harness.Vm.ShowUploadOverview);  // defaults on
            Assert.True(view.OverviewPanel.IsVisible);

            harness.Vm.ShowUploadOverview = false;       // the ✕ close button sets this
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.OverviewPanel.IsVisible);

            harness.Vm.ShowUploadOverview = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.OverviewPanel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Overview chevron (Task 12): a real LEFT release on the title-bar toggle collapses the stats area (rule 10) ──

    [AvaloniaFact]
    public void OverviewChevron_PointerReleased_TogglesIsOverviewExpanded_AndStatsAreaFollows()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            Assert.True(harness.Vm.IsOverviewExpanded);   // defaults expanded
            Assert.True(view.OverviewStatsArea.IsVisible);

            // A real left press+release on the chevron strip runs OverviewToggle_PointerReleased (the initial-
            // button guard passes only for Left), which flips IsOverviewExpanded → the stats area collapses.
            LeftClick(window, view.OverviewToggle);
            Assert.False(harness.Vm.IsOverviewExpanded);
            Assert.False(view.OverviewStatsArea.IsVisible);

            // ...and back.
            LeftClick(window, view.OverviewToggle);
            Assert.True(harness.Vm.IsOverviewExpanded);
            Assert.True(view.OverviewStatsArea.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Overview stat toggle (Task 12, rule 31): the checkable menu item two-way-writes Show* and the paired stat follows ──

    [AvaloniaFact]
    public void OverviewStatToggle_TwoWayWritesShowFlag_AndStatPairVisibilityFollows()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);
        try
        {
            ContextMenu menu = view.OverviewStatsArea.ContextMenu!;
            menu.DataContext = harness.Vm; // resolve the inherited VM binding for the (closed) menu

            var packagesItem = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal(MenuItemToggleType.CheckBox, packagesItem.ToggleType); // rule 31 glyph lever

            Assert.True(harness.Vm.ShowPackages);       // defaults on...
            Assert.True(view.PackagesStat.IsVisible);   // ...so the Packages stat pair shows

            // Rule 31: flip IsChecked (what a real click does) then raise Click (the Phase 5 Task 3 pattern).
            // The two-way IsChecked binding writes ShowPackages back to the VM.
            packagesItem.IsChecked = false;
            packagesItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(harness.Vm.ShowPackages);      // the toggle wrote through
            Assert.False(view.PackagesStat.IsVisible);  // and the paired stat pair collapsed (rule 33)

            packagesItem.IsChecked = true;
            packagesItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(harness.Vm.ShowPackages);
            Assert.True(view.PackagesStat.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Add→wizard (Task 12): the Add toolbar button opens a UploadWizardWindow via the factory seam ──

    [AvaloniaFact]
    public void AddButton_OpensUploadWizardWindow()
    {
        using VmHarness harness = new();
        harness.SeedPackage("Alpha pack", "alpha1.bin");
        (Window window, UploadsView view) = Show(harness.Vm);

        // App.Services is null under the test lifetime (the production ctor path is covered by the Task 7 DI
        // hand-construction test), so swap the factory for the internal VM-injection wizard ctor.
        UploadWizardWindow? opened = null;
        UploadWizardViewModel wizardVm = harness.BuildWizardViewModel();
        view.UploadWizardWindowFactory = _ => opened = new UploadWizardWindow(wizardVm);

        try
        {
            view.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(opened);
            Assert.True(opened!.IsVisible); // ShowDialog(owner) put the wizard on screen
        }
        finally
        {
            // AvaloniaFact discipline: close every window opened (headless windows are process-global).
            opened?.Close();
            window.Close();
        }
    }

    // ── Premium footer (Task 12): the jump resolves the parent Window's MainViewModel → Settings/Accounts ──

    [AvaloniaFact]
    public void PremiumFooter_JumpToAccounts_SetsSettingsTabAndAccountsCategory()
    {
        // A REAL MainViewModel over the head's full DI graph (the AvaloniaStartupDISmokeTests pattern), so the
        // footer's window-ancestor lookup lands a genuine MainViewModel + SettingsViewModel. The one registration
        // overridden is IUpdateService — the real Velopack-backed one needs a VelopackLocator (process-global,
        // set only in the packaged app), and the footer jump never touches update-checking.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-uploadsview-footer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ServiceCollection services = new();
        App.ConfigureServices(services, tempDir);
        services.AddSingleton<IUpdateService>(Mock.Of<IUpdateService>()); // last registration wins for GetRequiredService
        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            // The head creates the schema at app startup (not in ConfigureServices), so materialize it here —
            // MainViewModel's settings load reads the Setting table once the host window is shown.
            using (CSUploaderDbContext db = provider.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>().CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            // MainViewModel is a container-owned singleton and (Phase 9 ledger fix c) IDisposable, so the
            // provider.Dispose() in the finally below stops its 6h update timer and detaches its ctor
            // Localizer.Instance subscription — the VM graph no longer outlives this test, and the old
            // "never resolve inside a loop, it leaks a live graph + timer" warning is obsolete.
            MainViewModel main = provider.GetRequiredService<MainViewModel>();
            UploadsView view = new() { DataContext = main.UploadsViewModel };
            Window window = new() { Width = 900, Height = 600, Content = view, DataContext = main };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.NotEqual(2, main.SelectedTabIndex); // not already on Settings

                // PremiumAccountLink_PointerReleased delegates to this after the left-button guard; it resolves
                // the host Window's MainViewModel (WPF Window.GetWindow(this)) and jumps to Settings → Accounts.
                Assert.True(view.JumpToAccountsSettings());
                Assert.Equal(2, main.SelectedTabIndex);                        // Settings tab
                Assert.Equal(3, main.SettingsViewModel.SelectedCategoryIndex); // Accounts category
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            provider.Dispose();
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    // ── helpers ──

    private static void LeftClick(Window window, Visual target)
    {
        Point centre = CenterInWindow(target, window);
        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridColumn OrderColumnOf(UploadsView view)
        => view.uploadsGrid.Columns.First(c => Equals(c.Header, Localizer.Instance["Uploads_Col_Order"]));

    // Raises a real Ctrl+<key> KeyDown through the grid's TUNNEL handler (UploadsGrid_KeyDown) and returns
    // whether it was Handled — the copy interception mirrors the built-in DataGrid's ProcessCopyKey gestures.
    private static bool RaiseCopyGesture(UploadsView view, Key key)
    {
        KeyEventArgs e = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Control,
            Source = view.uploadsGrid,
        };
        view.uploadsGrid.RaiseEvent(e);
        Dispatcher.UIThread.RunJobs();
        return e.Handled;
    }

    private static string ReadClipboard(IClipboard clipboard)
        => clipboard.TryGetTextAsync().GetAwaiter().GetResult() ?? string.Empty;

    private static int CountOf(DataGridCollectionView view) => view.Count;

    private static (Window Window, UploadsView View) Show(UploadsViewModel vm)
    {
        // Wide enough that the leading columns (through Progress) are in the horizontal viewport — the
        // DataGrid virtualizes columns, so a narrow window leaves the progress cells unrealized.
        UploadsView view = new() { DataContext = vm };
        Window window = new() { Width = 2400, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridRow RowFor(DataGrid grid, object item)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));

    private static DataGridColumnHeader HeaderWithContent(UploadsView view, string content)
        => view.uploadsGrid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .First(h => Equals(h.Content, content));

    private static ToggleButton? LockToggleOf(DataGridColumnHeader header)
        => header.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == "LockToggle");

    private static Point CenterInWindow(Visual v, Visual window)
        => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window) ?? default;

    private static void RaiseClick(ToggleButton toggle)
    {
        // The chevron's Click handler (ExpandToggle_Click) reads the toggle's current IsChecked; drive it by
        // flipping IsChecked (above) then raising Click — a real templated-toggle pointer press is flaky
        // headless (the Task 1 pattern of splitting the handler from a synthesized click).
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // Real pointer drag on the header's right edge: hover the edge, press, move by dx, release.
    private static void DragColumnEdge(Window window, DataGridColumnHeader header, double dx)
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

    /// <summary>
    /// Builds a real <see cref="UploadsViewModel"/> over an in-memory SQLite DB with an inline dispatcher —
    /// the scratch-repo harness shape the Core Uploads VM tests use. Packages are seeded DIRECTLY into the
    /// VM's public <see cref="UploadsViewModel.VisibleRows"/> / <see cref="UploadsViewModel.Packages"/> (the
    /// same shape the VM's PackageAdded handler produces), avoiding the manager's fire-and-forget persistence
    /// so nothing races the scratch connection dispose; nothing is ever scheduled, so no upload runs. The
    /// <see cref="SettingRepository"/> backs the column-menu persistence path.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UploadScheduler _scheduler;
        private readonly PackageManager _manager;
        private readonly FileHosterLoginRepository _loginRepo;
        private readonly AppSettings _settings = new();
        private readonly string _tempDir;

        public VmHarness()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
                .UseSqlite(_connection)
                .Options;
            TestDbContextFactory factory = new(options);
            using (CSUploaderDbContext db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            UploadPackageFileRepository fileRepo = new(factory);
            UploadPackageRepository packageRepo = new(factory);
            _loginRepo = new(factory);
            SettingRepository settingRepo = new(factory);

            DefaultFileHosterRegistry registry = new([]);
            _scheduler = new UploadScheduler(_settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
            _manager = new PackageManager(_settings, _scheduler, packageRepo, fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);

            Vm = new UploadsViewModel(_manager, _settings, DialogMock.Object, new InlineDispatcher(), Mock.Of<IClipboardService>(), settingRepo);

            _tempDir = Path.Combine(Path.GetTempPath(), $"csu-uploadsview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public UploadsViewModel Vm { get; }

        /// <summary>The dialog service the VM's RemoveSelected confirmation flows through — verifiable so the
        /// Delete-key guard tests can assert the remove path did / did not fire (Times.Never / Times.Once).</summary>
        public Mock<IDialogService> DialogMock { get; } = new();

        /// <summary>Seeds a package (default expanded) and its files straight into the VM's collections — the
        /// exact row shape the VM's PackageAdded handler builds (package row followed by its file rows).</summary>
        public Package SeedPackage(string title, params string[] fileNames)
        {
            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
            PackageOptions opts = new()
            {
                Title = title,
                Logger = Mock.Of<IAppLogger>(),
                Settings = new AppSettings(),
                FileHosters = new() { { hoster, login } },
            };
            Package package = new(opts);

            List<PackageFile> files = [];
            foreach (string name in fileNames)
            {
                string path = Path.Combine(_tempDir, name);
                File.WriteAllBytes(path, [1]);
                files.Add(new PackageFile(package, path, hoster, login) { Name = name });
            }

            package.AddPackageFiles([.. files]);

            Vm.Packages.Add(package);
            Vm.VisibleRows.Add(package);
            if (package.IsExpanded)
            {
                foreach (PackageFile file in package)
                {
                    Vm.VisibleRows.Add(file);
                }
            }

            return package;
        }

        /// <summary>A scratch <see cref="UploadWizardViewModel"/> (the UploadWizardShellTests harness shape),
        /// for the Add-button test's wizard factory — the production ctor's <c>App.Services</c> path is null
        /// under the test lifetime, so the Add handler's factory seam is swapped for a wizard built from this.</summary>
        public UploadWizardViewModel BuildWizardViewModel()
            => new(_manager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), _settings);

        public void Dispose()
        {
            Vm.Dispose();
            _scheduler.Dispose();
            _connection.Dispose();
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        private static AttemptRunner BuildAttemptRunner()
        {
            DefaultFileHosterRegistry registry = new([]);
            Mock<IProxySource> proxy = new();
            proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
            Mock<IHttpHandlerFactory> hf = new();
            hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
                .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
            return new AttemptRunner(registry, proxy.Object, hf.Object);
        }

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }

    /// <summary>
    /// Deterministic <see cref="IUiDispatcher"/> for the view tests: Post/InvokeAsync run INLINE and the
    /// refresh timer is a stopped no-op (the view tests never tick it). Mirror of the Core InlineUiDispatcher.
    /// </summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => new NoopTimer();

        private sealed class NoopTimer : IUiTimer
        {
            public void Start()
            {
            }

            public void Stop()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
