// <copyright file="SettingsConnectionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="SettingsView"/> Connection panel (Phase 6 Task 5) — the
/// EDITABLE proxies grid. Rule 32 is PER-GRID and THIS grid is the exception: the load-bearing tests prove
/// the Host/Port/User/Password DataGridTextColumns, the Enabled DataGridCheckBoxColumn and the Type template
/// ComboBox all write BACK two-way (a blanket OneWay reflex — the Phase-5 read-only reflex — would silently
/// drop every proxy edit, the inverse of the Phase-5 corruption). Also: the priority reorder buttons, the
/// Details-button visibility, the Add/Test-All command wiring, the row context-menu SelectedItems parameters
/// and its row-vs-whitespace flip, the Delete key, and how the rows reach their VM (§Reality-check #19).
/// The rows' DataContext is the <see cref="ConnectionManagerViewModel"/> the SettingsViewModel exposes as
/// ConnectionManager; the SettingsView's own DataContext supplies SelectedCategoryIndex. Every window is closed in a
/// <c>finally</c> (headless windows are process-global).
/// </summary>
public class SettingsConnectionTests
{
    // ── The rule-37 regression guard: the editable text columns must NOT be OneWay ──
    // (the inverse of Phase 5's read-only guard — a OneWay here would drop every edit).

    [AvaloniaFact]
    public void EditableTextColumns_AreTwoWay_NotOneWay()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            foreach (string path in new[] { "Host", "Port", "Username", "Password" })
            {
                DataGridTextColumn col = TextColumn(view.ProxyGrid, path);
                var binding = Assert.IsType<CompiledBinding>(col.Binding);
                Assert.NotEqual(BindingMode.OneWay, binding.Mode); // Default (→TwoWay) or TwoWay — never OneWay
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EnabledCheckboxColumn_IsTwoWay_NotOneWay()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGridCheckBoxColumn onColumn = view.ProxyGrid.Columns.OfType<DataGridCheckBoxColumn>().Single();
            var binding = Assert.IsType<CompiledBinding>(onColumn.Binding);
            Assert.Equal("Enabled", binding.Path?.ToString());
            Assert.NotEqual(BindingMode.OneWay, binding.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Behavioral write-back: a real cell edit commits into the row VM AND its underlying Dto ──

    [AvaloniaFact]
    public void HostCell_Edit_CommitsTwoWay_ToRowAndDto()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("old.host", 8080, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            DataGridTextColumn hostColumn = TextColumn(grid, "Host");

            grid.SelectedItem = row0;
            grid.ScrollIntoView(row0, hostColumn);
            grid.CurrentColumn = hostColumn;
            Dispatcher.UIThread.RunJobs();

            Assert.True(grid.BeginEdit(), "BeginEdit should enter edit mode on the Host cell");
            Dispatcher.UIThread.RunJobs();

            // The Host cell realizes the column's two-way TextBox editor seeded with the current value; pick it
            // out by that seed (the row's other editors carry their own columns' values).
            TextBox editor = grid.GetVisualDescendants().OfType<TextBox>().First(tb => tb.Text == "old.host");
            editor.Text = "edited.example";
            Assert.True(grid.CommitEdit());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("edited.example", row0.Host);
            Assert.Equal("edited.example", row0.Dto.Host); // the edit survives into the Dto that persists
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TypeComboCell_TwoWay_WritesTypeToRowAndDto()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 8080, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            ComboBox typeCombo = RowFor(view.ProxyGrid, row0).GetVisualDescendants().OfType<ComboBox>().First();

            // The combo binds SelectedItem two-way to the row's Type — picking a new value writes it back.
            Assert.Equal(ProxyType.Http, typeCombo.SelectedItem);
            typeCombo.SelectedItem = ProxyType.Socks5;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ProxyType.Socks5, row0.Type);
            Assert.Equal(ProxyType.Socks5, row0.Dto.Type);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Priority buttons: wired to the grid's Move commands with the row as parameter, and they reorder ──

    [AvaloniaFact]
    public void PriorityButtons_WiredToMoveCommands_AndReorderCollection()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            var buttons = RowFor(view.ProxyGrid, row1).GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Classes.Contains("prio-btn"))
                .ToList();

            Assert.Equal(2, buttons.Count);
            Assert.Same(harness.Vm.MoveUpCommand, buttons[0].Command);
            Assert.Same(row1, buttons[0].CommandParameter);
            Assert.Same(harness.Vm.MoveDownCommand, buttons[1].Command);
            Assert.Same(row1, buttons[1].CommandParameter);

            // Behavioral: Move-up on row1 (index 1) floats it to the top.
            buttons[0].Command!.Execute(buttons[0].CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(row1, harness.Vm.Proxies[0]);
        }
        finally
        {
            window.Close();
        }
    }

    // ── ▲/▼ must reorder the rows the grid SHOWS, not only the collection underneath. The grid shows Proxies
    //    through a DataGridCollectionView, which does not act on a Move notification — so a Proxies.Move
    //    reordered the data and the saved priority while the grid kept the old order until its view was
    //    rebuilt, which used to happen on every visit to the Settings tab. ──

    [AvaloniaFact]
    public void PriorityButtons_ReorderTheRowsTheGridShows()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            harness.Vm.MoveUpCommand.Execute(row1);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { row1, row0 }, ShownOrder(view.ProxyGrid));

            harness.Vm.MoveDownCommand.Execute(row1);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { row0, row1 }, ShownOrder(view.ProxyGrid));
        }
        finally
        {
            window.Close();
        }
    }

    // ── A moved row must stay the selected one: Delete, Test and the Export-selected actions all act on
    //    the grid's SelectedItems, so losing it would leave them acting on nothing, or on another proxy.
    //    That is what a move made as a remove-and-reinsert does, and what a rebuilt view does. ──

    [AvaloniaFact]
    public void PriorityButtons_KeepTheMovedRowSelected()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            view.ProxyGrid.SelectedItem = row1;
            Dispatcher.UIThread.RunJobs();

            harness.Vm.MoveUpCommand.Execute(row1);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(row1, view.ProxyGrid.SelectedItem);
            Assert.Equal(new[] { row1 }, view.ProxyGrid.SelectedItems.Cast<ProxySettingItem>());
        }
        finally
        {
            window.Close();
        }
    }

    // ── ▲/▼ leave the selection as it was. Re-reading the order makes the grid select its current row
    //    again, and a click on empty space clears the selection but leaves that row current — so without
    //    putting the selection back, ▲ on the row just deselected would select it again. ──

    [AvaloniaFact]
    public void PriorityButtons_LeaveAClearedSelectionCleared()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            view.ProxyGrid.SelectedItem = row1;
            Dispatcher.UIThread.RunJobs();
            view.ProxyGrid.SelectedItems.Clear(); // what a click on the grid's empty area does
            Dispatcher.UIThread.RunJobs();

            ClickPriority(window, view.ProxyGrid, row1, up: true);

            Assert.Equal(new[] { row1, row0 }, ShownOrder(view.ProxyGrid));
            Assert.Empty(view.ProxyGrid.SelectedItems);
            Assert.Null(view.ProxyGrid.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    // ── ▲/▼ while a cell is being edited. Ticking "On" or typing in a cell opens an edit on the row that a
    //    click on ▲/▼ does not close — the button keeps the press to itself — and the grid's view refuses
    //    to re-read its order while an edit is open. The click ends the edit first, the way a click on
    //    another row does: what was typed is kept, and the row moves. ──

    [AvaloniaTheory]
    [InlineData("Host")]
    [InlineData("On")]
    public void PriorityButtons_CommitAnOpenEdit_ThenMoveTheRow(string column)
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            if (column == "Host")
            {
                ((TextBox)BeginEditOn(grid, row1, TextColumn(grid, "Host"))).Text = "host.edited";
            }
            else
            {
                ((CheckBox)BeginEditOn(grid, row1, grid.Columns.OfType<DataGridCheckBoxColumn>().Single())).IsChecked = false;
            }

            Dispatcher.UIThread.RunJobs();

            ClickPriority(window, grid, row1, up: true);

            Assert.Equal(new[] { row1, row0 }, harness.Vm.Proxies);
            Assert.Equal(new[] { row1, row0 }, ShownOrder(grid));
            Assert.Same(row1, grid.SelectedItem);
            Assert.False(IsEditing(grid));
            Assert.Equal(column == "Host" ? "host.edited" : "host.b", row1.Host);
            Assert.Equal(column != "On", row1.Enabled);
        }
        finally
        {
            window.Close();
        }
    }

    // ── …unless the edit cannot be committed. A cell holding a value it cannot take (letters in Port) keeps
    //    the grid in that cell, as it does when another row is clicked, so ▲/▼ do nothing until it is
    //    fixed — rather than move the row in the list while the grid, stuck in the edit, cannot show it. ──

    [AvaloniaFact]
    public void PriorityButtons_WithAnInvalidCellOpen_MoveNothing()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            DataGridTextColumn port = TextColumn(grid, "Port");
            TextBox editor = (TextBox)BeginEditOn(grid, row1, port);
            editor.Text = "not a port";
            Dispatcher.UIThread.RunJobs();

            ClickPriority(window, grid, row1, up: true);

            Assert.Equal(new[] { row0, row1 }, harness.Vm.Proxies);
            Assert.Equal(new[] { row0, row1 }, ShownOrder(grid));
            Assert.Same(editor, port.GetCellContent(RowFor(grid, row1))); // still in the edit, showing its error
        }
        finally
        {
            window.Close();
        }
    }

    // ── A move made in code while a cell is being edited must not throw out of Proxies.Move: the grid is kept
    //    in step from inside the move, and its view refuses to re-read its order while an edit is open. ──

    [AvaloniaFact]
    public void AMoveMadeInCode_WhileACellIsBeingEdited_DoesNotThrow()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            BeginEditOn(view.ProxyGrid, row1, TextColumn(view.ProxyGrid, "Host"));

            harness.Vm.MoveUpCommand.Execute(row1); // no click, so nothing ends the edit first
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { row1, row0 }, harness.Vm.Proxies);
        }
        finally
        {
            window.Close();
        }
    }

    // ── ▲/▼ drop a column sort, as reordering the Uploads queue does: under a sort the moved row stays where
    //    the sort puts it, so the new priority would not show — and the sort now lasts across tab switches,
    //    so it would not show for the rest of the session. ──

    [AvaloniaFact]
    public void PriorityButtons_DropAColumnSort_SoTheNewOrderShows()
    {
        using ConnHarness harness = new();
        ProxySettingItem c = Row("c.example", 1, ProxyType.Http, enabled: true);
        ProxySettingItem a = Row("a.example", 2, ProxyType.Http, enabled: true);
        ProxySettingItem b = Row("b.example", 3, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(c);
        harness.Vm.Proxies.Add(a);
        harness.Vm.Proxies.Add(b);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            grid.CollectionView.SortDescriptions.Add(
                DataGridSortDescription.FromPath(nameof(ProxySettingItem.Host), ListSortDirection.Ascending));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { a, b, c }, ShownOrder(grid));

            ClickPriority(window, grid, b, up: true); // priority c, a, b → c, b, a

            Assert.Empty(grid.CollectionView.SortDescriptions);
            Assert.Equal(new[] { c, b, a }, ShownOrder(grid));
        }
        finally
        {
            window.Close();
        }
    }

    // ── An edit still open when the Settings tab is left. Coming back, the grid cancels it, but a proxy row
    //    cannot cancel an edit (it is not an IEditableObject): the grid let go of the edit while its view
    //    kept it open, and from then on every column sort and every ▲/▼ on the grid failed. Each visit
    //    used to give the grid a new view, which hid this. ──

    [AvaloniaFact]
    public void ProxyGrid_BackWithAnEditLeftOpen_StillReordersAndSorts()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 1, ProxyType.Http, enabled: true);
        ProxySettingItem row1 = Row("host.b", 2, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        harness.Vm.Proxies.Add(row1);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            ((TextBox)BeginEditOn(grid, row1, TextColumn(grid, "Port"))).Text = "not a port";
            Dispatcher.UIThread.RunJobs();

            window.Content = null; // leaving the tab takes the view out of the window…
            Dispatcher.UIThread.RunJobs();
            window.Content = view; // …and coming back puts it back
            Dispatcher.UIThread.RunJobs();

            ClickPriority(window, grid, row1, up: true);
            Assert.Equal(new[] { row1, row0 }, ShownOrder(grid));

            grid.CollectionView.SortDescriptions.Add(
                DataGridSortDescription.FromPath(nameof(ProxySettingItem.Host), ListSortDirection.Ascending));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { row0, row1 }, ShownOrder(grid));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Details button hidden when the row has no captured transaction, shown when it does ──

    [AvaloniaFact]
    public void DetailsButton_HiddenWhenNoTestDetails_VisibleWithTransaction()
    {
        using ConnHarness harness = new();
        ProxySettingItem noDetails = Row("host.a", 8080, ProxyType.Http, enabled: true);
        ProxySettingItem withDetails = Row("host.b", 8080, ProxyType.Http, enabled: true);
        withDetails.TestTransaction = new HttpTransaction { Method = "GET", Url = "https://example.test/" };
        harness.Vm.Proxies.Add(noDetails);
        harness.Vm.Proxies.Add(withDetails);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            Button? hidden = DetailsButton(view.ProxyGrid, noDetails, harness.Vm);
            Button? shown = DetailsButton(view.ProxyGrid, withDetails, harness.Vm);

            // A hidden cell is "not visible" whether Avalonia keeps a collapsed button or omits it.
            Assert.True(hidden is null || !hidden.IsVisible);
            Assert.NotNull(shown);
            Assert.True(shown!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Add / Test All plain buttons bind directly to the inherited VM commands ──

    [AvaloniaFact]
    public void AddAndTestAllButtons_BoundToVmCommands()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            // The context-menu Add is a MenuItem (not a Button, and not bound until opened), so exactly the
            // bottom-bar Button carries each command.
            Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, harness.Vm.AddCommand));
            Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, harness.Vm.TestAllCommand));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 19: the SelectedItems-taking context-menu commands carry the grid's live SelectedItems ──

    [AvaloniaFact]
    public void ContextMenu_SelectedItemsParameters_AreTheGridsSelectedItems()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            Assert.Same(view.ProxyGrid.SelectedItems, view.ProxyContextTestItem.CommandParameter);
            Assert.Same(view.ProxyGrid.SelectedItems, view.ProxyContextRemoveItem.CommandParameter);
            Assert.Same(view.ProxyGrid.SelectedItems, view.ProxyContextExportSelectedToTextItem.CommandParameter);
            Assert.Same(view.ProxyGrid.SelectedItems, view.ProxyContextExportSelectedToFileItem.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 18: the row-only context items hide on a whitespace right-click, show on a row ──

    [AvaloniaFact]
    public void ContextMenu_RowOnlyItems_HiddenOnWhitespace_ShownOnRow()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 8080, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            // Right-click on a row → the Test/Remove/Export-selected items (and their separators) show.
            view.ApplyProxyRightClickTarget(RowFor(view.ProxyGrid, row0));
            Assert.True(view.ApplyProxyContextRowItemVisibility());
            Assert.True(view.ProxyContextTestItem.IsVisible);
            Assert.True(view.ProxyContextRemoveItem.IsVisible);
            Assert.True(view.ProxyContextRowSeparator.IsVisible);
            Assert.True(view.ProxyContextExportSelectedToTextItem.IsVisible);
            Assert.True(view.ProxyContextExportSelectedToFileItem.IsVisible);

            // Right-click on empty space (the grid itself, not a row) → those items hide; Add/Import/Export stay.
            view.ApplyProxyRightClickTarget(view.ProxyGrid);
            Assert.False(view.ApplyProxyContextRowItemVisibility());
            Assert.False(view.ProxyContextTestItem.IsVisible);
            Assert.False(view.ProxyContextRemoveItem.IsVisible);
            Assert.False(view.ProxyContextExportSelectedToTextItem.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 24: the Delete key removes the selection through RemoveSelectedCommand ──
    // (FIX 1: this EDITABLE grid's binding is the editor-guard wrapper, which delegates to RemoveSelectedCommand.)

    [AvaloniaFact]
    public void DeleteKey_WiredToRemoveSelected_WithSelectedItemsParameter()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            KeyBinding binding = Assert.Single(view.ProxyGrid.KeyBindings);
            Assert.Equal(Key.Delete, binding.Gesture.Key);
            var guarded = Assert.IsType<DataGridDeleteKeyGuard.EditorGuardedCommand>(binding.Command);
            Assert.Same(harness.Vm.RemoveSelectedCommand, guarded.Inner);
            Assert.Same(view.ProxyGrid.SelectedItems, binding.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    }

    // ── FIX 1 (MAJOR): while a cell editor is focused, Delete edits text and does NOT remove the edited row ──

    [AvaloniaFact]
    public void DeleteKey_WhileCellEditorFocused_EditsText_DoesNotRemove()
    {
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("old.host", 8080, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            DataGridTextColumn hostColumn = TextColumn(grid, "Host");

            grid.SelectedItem = row0;
            grid.ScrollIntoView(row0, hostColumn);
            grid.CurrentColumn = hostColumn;
            Dispatcher.UIThread.RunJobs();

            Assert.True(grid.BeginEdit(), "BeginEdit should enter edit mode on the Host cell");
            Dispatcher.UIThread.RunJobs();

            TextBox editor = grid.GetVisualDescendants().OfType<TextBox>().First(tb => tb.Text == "old.host");
            editor.Text = "abc";
            editor.CaretIndex = 0;
            editor.Focus();
            Dispatcher.UIThread.RunJobs();

            // The guard sees the focused cell editor → CanExecute is false, so KeyBinding.TryHandle declines
            // WITHOUT marking the KeyDown Handled and the keystroke falls through to the editing TextBox.
            var guarded = (DataGridDeleteKeyGuard.EditorGuardedCommand)Assert.Single(grid.KeyBindings).Command;
            Assert.True(guarded.IsCellEditorFocused());
            Assert.False(guarded.CanExecute(grid.SelectedItems));

            // A real Delete keystroke forward-deletes the char at the caret (WPF parity), and the remove
            // confirmation is never shown — the remove command did not fire.
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("bc", editor.Text);
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
        using ConnHarness harness = new();
        ProxySettingItem row0 = Row("host.a", 8080, ProxyType.Http, enabled: true);
        harness.Vm.Proxies.Add(row0);
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            DataGrid grid = view.ProxyGrid;
            grid.SelectedItem = row0; // the live SelectedItems now carries row0
            Dispatcher.UIThread.RunJobs();

            var guarded = (DataGridDeleteKeyGuard.EditorGuardedCommand)Assert.Single(grid.KeyBindings).Command;

            // No cell editor is focused → the guard delegates straight to RemoveSelectedCommand.
            Assert.False(guarded.IsCellEditorFocused());
            Assert.True(guarded.CanExecute(grid.SelectedItems));

            // Executing it runs the remove path — the opt-out confirmation is shown for the selected proxy
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

    // ── The grid dims (proxies-off class) when Use-proxies is off, via a conditional-class binding ──

    [AvaloniaFact]
    public void ProxiesDisabled_TogglesTheDimmingClass()
    {
        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        harness.Vm.ProxiesEnabled = true;
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            // Classes.proxies-off="{Binding !ProxiesEnabled}" — set when the master switch is off (opacity 0.45),
            // the port of the WPF DataGrid.Style DataTrigger on ProxiesEnabled (rule 40 per-value → class bind).
            Assert.DoesNotContain("proxies-off", view.ProxyGrid.Classes);

            harness.Vm.ProxiesEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("proxies-off", view.ProxyGrid.Classes);

            harness.Vm.ProxiesEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("proxies-off", view.ProxyGrid.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    // ── §Reality-check #19: the panel's CONTENT resolves the ConnectionManagerViewModel through
    //    SettingsViewModel.ConnectionManager, while the panel Grid itself stays on the SettingsViewModel ──

    [AvaloniaFact]
    public void PanelContent_ResolvesConnectionVm_ThroughTheSettingsVm_WhileTheGridKeepsTheSettingsVm()
    {
        // Also the guard on the page's DataContexts: the view compiles its bindings against
        // SettingsViewModel and the rows take its ConnectionManager, so a mismatch would fail silently — a
        // failed binding only writes to debug output. The sink turns that into a test failure.
        using BindingErrorSink sink = BindingErrorSink.Install();

        using ConnHarness harness = new();
        harness.Vm.Proxies.Add(Row("host.a", 8080, ProxyType.Http, enabled: true));
        (Window window, SettingsView view) = Show(harness.SettingsVm);
        try
        {
            // Each row child binds DataContext to SettingsViewModel.ConnectionManager.
            Assert.Same(harness.Vm, view.ProxyGrid.DataContext);

            // …but the Grid itself deliberately does NOT, and inherits the SettingsViewModel instead.
            // That is what lets its IsVisible bind straight to SelectedCategoryIndex. With the override
            // on the Grid, IsVisible had to reach back up through
            // $visualParent[UserControl].DataContext, which is evaluated before the DataContext arrives
            // (MainWindow hosts this view inside a TabItem) and logged two binding errors every startup.
            Assert.Same(harness.SettingsVm, view.ConnectionPanel.DataContext);
        }
        finally
        {
            window.Close();
        }

        Assert.Empty(sink.Errors);
    }

    // ── helpers ──

    // CompiledBinding, not Binding: the proxy grid's rows are typed (the compiler infers
    // ProxySettingItem from the ConnectionManagerViewModel.Proxies ItemsSource), so its columns are
    // compiled bindings. Matching on the compiled type is also what pins them there — a column that
    // fell back to reflection would no longer be found by this helper.
    private static DataGridTextColumn TextColumn(DataGrid grid, string path)
        => grid.Columns.OfType<DataGridTextColumn>()
            .First(c => c.Binding is CompiledBinding b && b.Path?.ToString() == path);

    private static DataGridRow RowFor(DataGrid grid, ProxySettingItem item)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));

    /// <summary>The proxies in the order the grid shows them, top to bottom.</summary>
    private static ProxySettingItem[] ShownOrder(DataGrid grid)
        => [.. grid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is ProxySettingItem)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => (ProxySettingItem)r.DataContext!)];

    /// <summary>Opens an edit on <paramref name="row"/>'s cell in <paramref name="column"/>; returns its editor.</summary>
    private static Control BeginEditOn(DataGrid grid, ProxySettingItem row, DataGridColumn column)
    {
        grid.SelectedItem = row;
        grid.ScrollIntoView(row, column);
        grid.CurrentColumn = column;
        Dispatcher.UIThread.RunJobs();
        Assert.True(grid.BeginEdit(), "BeginEdit should open an edit on the cell");
        Dispatcher.UIThread.RunJobs();
        return column.GetCellContent(RowFor(grid, row));
    }

    /// <summary>
    /// Clicks <paramref name="row"/>'s ▲ (or ▼) with the pointer, the way a user does: the press lands on
    /// the button, not on the cell, and the button's Click runs before its command. Not
    /// <c>HeadlessInput.Click</c>: that raises the Click alone, and never runs the command.
    /// </summary>
    private static void ClickPriority(Window window, DataGrid grid, ProxySettingItem row, bool up)
    {
        Button button = RowFor(grid, row).GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("prio-btn"))
            .ElementAt(up ? 0 : 1);
        Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The priority button is not in the window.");
        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Whether the grid's view still holds an edit open — while it does, it refuses to re-sort.</summary>
    private static bool IsEditing(DataGrid grid)
        => grid.CollectionView is DataGridCollectionView { IsEditingItem: true };

    private static Button? DetailsButton(DataGrid grid, ProxySettingItem item, ConnectionManagerViewModel vm)
        => RowFor(grid, item).GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.Command, vm.ShowTestDetailsCommand));

    private static ProxySettingItem Row(string host, int port, ProxyType type, bool enabled, string? user = null, string? pass = null)
        => new(new ProxySettingDto { Host = host, Port = port, Type = type, Enabled = enabled, Username = user, Password = pass });

    // ── The panel's visibility must not reach up through a DataContext that can be transiently null.
    //
    //    In the app this logged, on every startup:
    //      [Binding] An error occurred binding 'IsVisible' to
    //      '$visualParent[UserControl].DataContext.SelectedCategoryIndex' at 'DataContext':
    //      'Value is null.' (Grid #ConnectionPanel)
    //
    //    MainWindow hosted <views:SettingsView DataContext="{Binding SettingsViewModel}" /> inside a
    //    TabItem (that binding lives on the TabItem now), so the control was in the visual tree before
    //    the binding resolved, and the explicit `.DataContext.` hop was evaluated against null.
    //
    //    This is asserted on the MARKUP rather than at runtime, deliberately. Three headless
    //    reproductions were tried — assigning the DataContext late, binding it late (which does
    //    suppress inheritance, unlike assignment), and doing so inside a TabControl realised on
    //    selection — and NONE reproduced the null window; each passed against the original markup too.
    //    A runtime test that cannot fail is worse than none, so what is guarded here is the property
    //    that makes the error impossible: this panel binds its visibility to the inherited DataContext,
    //    with no ancestor hop, exactly as every sibling panel does. ──

    [Fact]
    public void ConnectionPanelVisibility_DoesNotReachThroughAnAncestorDataContext()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoXaml.FindRepoRoot(), "src", "CSUploader", "Views", "SettingsView.axaml"));

        int panel = xaml.IndexOf("x:Name=\"ConnectionPanel\"", StringComparison.Ordinal);
        Assert.True(panel > 0, "ConnectionPanel not found — did the panel get renamed?");

        // The Grid's own attributes run to the end of its opening tag.
        int tagEnd = xaml.IndexOf('>', panel);
        string gridTag = xaml[panel..tagEnd];

        Assert.Contains("IsVisible=\"{Binding SelectedCategoryIndex", gridTag, StringComparison.Ordinal);
        Assert.DoesNotContain("AncestorType=UserControl", gridTag, StringComparison.Ordinal);

        // …and it must not re-acquire a DataContext override, which is what forced the hop before:
        // the panel's visibility lives on the SettingsViewModel, so the Grid stays on it and the three
        // row children carry the ConnectionManagerViewModel instead.
        Assert.DoesNotContain("DataContext=", gridTag, StringComparison.Ordinal);
    }

    private static (Window Window, SettingsView View) Show(SettingsViewModel settings)
    {
        // The view gets a REAL SettingsViewModel: SettingsView declares x:DataType="vm:SettingsViewModel",
        // so its generated accessors CAST, and a stand-in would fail every binding on the page silently.
        // Its ConnectionManager is what the Connection rows bind.
        //
        // Wide enough that every proxy column (~1010px min) is in the horizontal viewport — the DataGrid
        // virtualizes columns, so a narrower window would leave the trailing Status/Details cells unrealized.
        SettingsView view = new() { DataContext = settings };
        Window window = new() { Width = 1600, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>
    /// A real <see cref="ConnectionManagerViewModel"/> over an in-memory SQLite DB — the scratch-repo harness
    /// the WPF <c>ConnectionManagerViewModelTests</c> uses. LoadAsync is intentionally NOT called, so the VM's
    /// auto-save stays suppressed and the tests drive Proxies directly.
    /// </summary>
    private sealed class ConnHarness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public ConnHarness()
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

            ProxySettingRepository repo = new(factory);
            ProxyManager manager = new(repo, Mock.Of<IAppLogger>(), new AppSettings { ProxiesEnabled = true });
            Vm = new ConnectionManagerViewModel(repo, manager, DialogMock.Object, Mock.Of<IAppLogger>(), Mock.Of<IUiDispatcher>())
            {
                ProxiesEnabled = true,
            };

            // The real view-model the view binds, with this harness's ConnectionManagerViewModel as its
            // ConnectionManager. Category 2 is the Connection page, which is what every test here looks at.
            SettingsVm = new SettingsViewModel(
                new SettingRepository(factory),
                new AccountManagerViewModel(
                    new FileHosterLoginRepository(factory),
                    DialogMock.Object,
                    Mock.Of<IAppLogger>(),
                    Mock.Of<IAccountVerifier>()),
                new AppSettings(),
                DialogMock.Object,
                Mock.Of<IAppLogger>(),
                connectionManager: Vm)
            {
                SelectedCategoryIndex = 2,
            };
        }

        public ConnectionManagerViewModel Vm { get; }

        /// <summary>The real SettingsViewModel the view's compiled bindings resolve against.</summary>
        public SettingsViewModel SettingsVm { get; }

        /// <summary>The dialog service the VM's RemoveSelected confirmation flows through — verifiable so the
        /// Delete-key guard tests can assert the remove path did / did not fire (Times.Never / Times.Once).</summary>
        public Mock<IDialogService> DialogMock { get; } = new();

        public void Dispose() => _connection.Dispose();

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
