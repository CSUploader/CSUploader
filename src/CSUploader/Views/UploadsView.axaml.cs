// <copyright file="UploadsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.Lib.Localization;
using CSUploader.Lib.UI;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// The Uploads tab: the flat, filtered DataGrid over the VM's interleaved package/file rows. Avalonia port
/// of the WPF <c>UploadsView</c> (Phase 6 Task 10). The filtered collection view is built in the head (the
/// Core <see cref="UploadsViewModel"/> is framework-free and only exposes the raw
/// <see cref="UploadsViewModel.VisibleRows"/> collection + a <see cref="UploadsViewModel.MatchesFilter"/>
/// predicate + a <see cref="UploadsViewModel.FilterInvalidated"/> signal). The custom column header
/// (lock toggle + drag-resize + sort) is the Task 1 recipe; the column menu / persistence is the shared
/// Phase 5 helper. The ~40-item context menu, the editable Order cell (BeginningEdit/CellEditEnding guards),
/// the right-click target recorder + menu suppression, the package-expanding Ctrl+C/menu copy and the Delete
/// key are Task 11. The JD2 overview panel (with its 12-toggle stats context menu), the premium footer jump and
/// the Add→wizard wiring are Task 12 — which completes UploadsView.
/// </summary>
public partial class UploadsView : UserControl
{
    // Snapshot of the XAML-default column state, captured at first Loaded before any persisted overrides
    // are applied; used by the column menu's "Reset columns" entry.
    private Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState>? _defaultColumnState;
    private bool _columnsWired;

    // The filtered view over the current VM's VisibleRows, built ONCE per VM and kept for its lifetime.
    // DataGridCollectionView subscribes to the source's CollectionChanged in its ctor and never removes
    // that handler (it is not IDisposable), so re-minting it per FilterInvalidated would permanently orphan
    // a subscriber on the long-lived VisibleRows collection (the Phase 5 leak class). Filter state is
    // re-established IN PLACE via Refresh(); the view is never rebuilt. Tracked alongside the VM it was
    // built for so a DataContext swap can unsubscribe the old VM's event before wiring the new one.
    private DataGridCollectionView? _rowsView;
    private UploadsViewModel? _wiredViewModel;

    // True when the last right-button press landed on a package/file row (vs empty space below the rows).
    // The ContextMenu.Opening handler reads it to suppress the menu on a whitespace right-click — Opening
    // carries no pointer source, so the decision is recorded here at press time (rule 18). The actual row
    // selection is done by the SelectRowOnRightClick behavior on the grid (Task 10, verified here — prep 8);
    // this only captures the target for suppression.
    private bool _rightClickOnItem;

    // The editable "Order" column, resolved once by header text. x:Name does NOT compile on an Avalonia
    // DataGridColumn (it is not a namescope StyledElement — AVLN2000, the Task 10 deviation), so the
    // BeginningEdit/CellEditEnding guards reference this captured field instead of the plan's literal
    // e.Column == OrderColumn. The 21 Uploads_Col_* headers are unique (confirmed Task 10), so the
    // header-text match is unambiguous.
    private DataGridColumn? _orderColumn;

    // The "Name" column, resolved the same way for the package-rename edit path.
    private DataGridColumn? _nameColumn;

    private bool _deleteWired;

    public UploadsView()
    {
        InitializeComponent();

        // Rule 19: the SelectedItems-carrying command parameters are wired in code-behind. The grid's
        // SelectedItems is one live IList for the control's lifetime — the same instance the WPF
        // PlacementTarget.SelectedItems binding resolved to. The toolbar Remove button + the nine
        // context-menu items that act on the whole selection all point at that one instance.
        RemoveButton.CommandParameter = uploadsGrid.SelectedItems;
        StartMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        ForceStartMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        StopMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        SkipMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        ResetMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        OpenSourceDirMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        OpenUrlMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        SetSpeedLimitMenuItem.CommandParameter = uploadsGrid.SelectedItems;
        RemoveMenuItem.CommandParameter = uploadsGrid.SelectedItems;

        // Resolve the Order column by its (unique) localized header text (reference-capture, per the brief).
        _orderColumn = ResolveOrderColumn();

        // Right-click target recorder (rule 18): TUNNEL so it runs before ContextRequested opens the menu.
        // Records _rightClickOnItem; the SelectRowOnRightClick behavior (also tunnel, from Task 10) performs
        // the selection itself.
        uploadsGrid.AddHandler(InputElement.PointerPressedEvent, UploadsGrid_PointerPressed, RoutingStrategies.Tunnel);

        // Ctrl+C / Ctrl+Insert = the package-expanding copy (the DIVERGENCE from UploadedView): a TUNNEL KeyDown
        // intercept that runs the custom copy and marks the event Handled, which suppresses the DataGrid's
        // built-in copy (DataGrid_KeyDown early-returns on e.Handled — confirmed via ILSpy). The built-in copy
        // routes BOTH gestures to ProcessCopyKey and would otherwise serialize only SelectedItems with no child
        // expansion — WPF expanded on both (ApplicationCommands.Copy), so both must be intercepted here.
        uploadsGrid.AddHandler(InputElement.KeyDownEvent, UploadsGrid_KeyDown, RoutingStrategies.Tunnel);

        // Editable Order cell (prep 7): the grid is IsReadOnly=False ONLY so the Order cell can be typed
        // into; BeginningEdit cancels every other edit and CellEditEnding commits the typed position.
        uploadsGrid.BeginningEdit += UploadsGrid_BeginningEdit;
        uploadsGrid.CellEditEnding += UploadsGrid_CellEditEnding;

        uploadsGrid.Loaded += OnGridLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The column menu, exposed for the headless tests to assert it attached with the first
    /// (anchor Name) column disabled.</summary>
    internal ContextMenu? ColumnMenu { get; private set; }

    /// <summary>The filtered view, exposed for the durable-view leak regression test.</summary>
    internal DataGridCollectionView? RowsView => _rowsView;

    /// <summary>
    /// Builds the DataGrid's filtered collection view in the head. The view wraps the VM's raw
    /// <see cref="UploadsViewModel.VisibleRows"/>, applies the VM's <see cref="UploadsViewModel.MatchesFilter"/>
    /// predicate as its filter, and refreshes in place whenever the VM raises
    /// <see cref="UploadsViewModel.FilterInvalidated"/> — the head-side stand-in for the WPF
    /// <c>ICollectionView</c> the framework-free ViewModel no longer owns. Built ONCE per VM (never
    /// re-minted — see <see cref="_rowsView"/>).
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wiredViewModel is not null)
        {
            _wiredViewModel.FilterInvalidated -= ViewModel_FilterInvalidated;
            _wiredViewModel = null;
            _rowsView = null;
        }

        if (DataContext is not UploadsViewModel vm)
        {
            uploadsGrid.ItemsSource = null;
            return;
        }

        _rowsView = new DataGridCollectionView(vm.VisibleRows)
        {
            Filter = vm.MatchesFilter,
        };
        vm.FilterInvalidated += ViewModel_FilterInvalidated;
        _wiredViewModel = vm;
        uploadsGrid.ItemsSource = _rowsView;
        WireDeleteKeyBinding(vm);
    }

    private void ViewModel_FilterInvalidated(object? sender, EventArgs e) => _rowsView?.Refresh();

    /// <summary>
    /// Delete key → <see cref="UploadsViewModel.RemoveSelectedCommand"/> (rule 24). A <see cref="KeyBinding"/>
    /// is a non-DataContext AvaloniaObject on 11.3.18, so it is wired in code-behind where the VM command and
    /// the live SelectedItems are both in hand (parameter per rule 19). The built-in DataGrid does NOT handle
    /// Delete (ILSpy: absent from ProcessDataGridKey), so this never fights a built-in row deletion. The grid is
    /// EDITABLE (the Order cell), so the binding is built through <see cref="DataGridDeleteKeyGuard"/>: while the
    /// Order CellEditingTemplate TextBox holds focus, Delete edits text instead of removing rows (WPF parity).
    /// </summary>
    private void WireDeleteKeyBinding(UploadsViewModel vm)
    {
        if (_deleteWired)
        {
            return;
        }

        _deleteWired = true;
        uploadsGrid.KeyBindings.Add(DataGridDeleteKeyGuard.CreateDeleteKeyBinding(
            uploadsGrid, vm.RemoveSelectedCommand, uploadsGrid.SelectedItems));
    }

    /// <summary>
    /// The Name-column chevron toggle: sets <see cref="Package.IsExpanded"/> so the VM inserts/removes the
    /// package's file rows in <see cref="UploadsViewModel.VisibleRows"/>. Only package rows carry a live
    /// chevron (it is Opacity-0 + non-hit-testable on file rows).
    /// </summary>
    private void ExpandToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: Package package } btn)
        {
            package.IsExpanded = btn.IsChecked == true;
        }
    }

    private async void OnGridLoaded(object? sender, RoutedEventArgs e)
    {
        // Loaded can fire more than once (tab switches re-attach the control); wire columns only once.
        if (_columnsWired || DataContext is not UploadsViewModel vm || vm.SettingRepo is not { } repo)
        {
            return;
        }

        _columnsWired = true;

        // Capture XAML defaults *before* applying persisted overrides so "Reset columns" can restore them.
        _defaultColumnState = DataGridColumnVisibilityPersistence.CaptureCurrentState(uploadsGrid);
        await DataGridColumnVisibilityPersistence.ApplyAsync(uploadsGrid, repo, SettingKey.UploadsTabHiddenColumns);

        ColumnMenu = DataGridColumnMenu.Build(
            uploadsGrid,
            _defaultColumnState,
            repo,
            SettingKey.UploadsTabHiddenColumns,
            vm.DialogServiceForView,
            "Uploads_ResetColumns_Message",
            "Uploads_ResetColumns_Title");
        DataGridColumnMenu.AttachToHeaders(uploadsGrid, ColumnMenu);

        // Persist column reorders the user does after the initial Apply.
        uploadsGrid.ColumnDisplayIndexChanged += async (_, _) =>
            await DataGridColumnVisibilityPersistence.PersistAsync(uploadsGrid, repo, SettingKey.UploadsTabHiddenColumns);
    }

    /// <summary>
    /// Toggles the column-width lock for the column whose header carries the clicked lock toggle. Splits the
    /// body into <see cref="ApplyColumnLock"/> so a headless test can drive it after setting
    /// <see cref="ToggleButton.IsChecked"/> (raising a real pointer click on a templated toggle is flaky
    /// headlessly). Mirrors the WPF <c>ColumnLock_Click</c> (UploadsView.xaml.cs:300-317).
    /// </summary>
    private void ColumnLock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            ApplyColumnLock(toggle);
        }
    }

    /// <summary>
    /// Sets the owning column's <see cref="DataGridColumn.CanUserResize"/> from the lock toggle — false when
    /// locked, which makes the header's built-in drag-resize a no-op for that column. <c>OwningColumn</c> /
    /// <c>HeaderCell</c> are internal on 11.3.13, so the header is associated to its column by matching the
    /// public <c>Content</c> to a column's public <see cref="DataGridColumn.Header"/> — the 21 Uploads_Col_*
    /// headers are unique (confirmed), so content-match is unambiguous.
    /// </summary>
    internal void ApplyColumnLock(ToggleButton toggle)
    {
        if (toggle.FindAncestorOfType<DataGridColumnHeader>() is not { } header)
        {
            return;
        }

        DataGridColumn? column = uploadsGrid.Columns.FirstOrDefault(c => Equals(c.Header, header.Content));
        if (column is not null)
        {
            column.CanUserResize = toggle.IsChecked != true;
        }
    }

    // ── Add → Upload Wizard (Task 12) ──

    /// <summary>
    /// Test seam for the Add→wizard construction. <see langword="null"/> in production, where the handler news
    /// the wizard exactly as the WPF opener does - <c>new UploadWizardWindow(vm)</c>, whose ctor resolves the
    /// DI-registered (Transient) <see cref="UploadWizardViewModel"/> from <c>App.Services</c>. The headless test sets
    /// this to a scratch-VM wizard because <c>App.Services</c> is <see langword="null"/> under the test
    /// lifetime (that path is separately covered by the Task 7 DI resolution test).
    /// </summary>
    internal Func<UploadsViewModel, UploadWizardWindow>? UploadWizardWindowFactory { get; set; }

    /// <summary>
    /// Add toolbar button → opens the Upload Wizard as a dialog on the main window. Mirrors the WPF
    /// <c>AddUploadButton_Click</c> (UploadsView.xaml.cs:223-233): <c>new UploadWizardWindow(vm)</c> owned by the
    /// parent window, shown modally. The dialog result is discarded — the WPF opener ignores <c>ShowDialog()</c>'s
    /// bool too (the wizard commits its own package on <see cref="UploadWizardViewModel.Completed"/>).
    /// </summary>
    private void AddUploadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UploadsViewModel vm || this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        UploadWizardWindow wizard = UploadWizardWindowFactory?.Invoke(vm) ?? new UploadWizardWindow(vm);
        _ = wizard.ShowDialog(owner);
    }

    // ── Upload Overview panel (Task 12) ──

    /// <summary>The ✕ button hides the whole overview panel (mirror the WPF <c>OverviewCloseButton_Click</c>,
    /// UploadsView.xaml.cs:235-241).</summary>
    private void OverviewCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UploadsViewModel vm)
        {
            vm.ShowUploadOverview = false;
        }
    }

    /// <summary>
    /// The title-bar chevron collapses/expands the stats row (leaving the title bar), on a LEFT release only
    /// (rule 10 — WPF <c>MouseLeftButtonUp</c> → <c>PointerReleased</c> + the initial-button guard). Mirrors the
    /// WPF <c>OverviewToggle_MouseLeftButtonUp</c> (UploadsView.xaml.cs:248-255).
    /// </summary>
    private void OverviewToggle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && DataContext is UploadsViewModel vm)
        {
            vm.IsOverviewExpanded = !vm.IsOverviewExpanded;
            e.Handled = true;
        }
    }

    // ── Premium footer jump (Task 12) ──

    /// <summary>
    /// The premium footer link jumps to Settings → Accounts, on a LEFT release only (rule 10). Mirrors the WPF
    /// <c>PremiumAccountLink_Click</c> (UploadsView.xaml.cs:410-418).
    /// </summary>
    private void PremiumAccountLink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && JumpToAccountsSettings())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Switches the host window to the Settings tab and selects the Accounts category. Resolves the parent
    /// <see cref="Window"/>'s <see cref="MainViewModel"/> (the Avalonia stand-in for WPF's
    /// <c>Window.GetWindow(this)</c>); returns <see langword="false"/> when no <see cref="MainViewModel"/> is
    /// reachable (e.g. the view is hosted outside the main window). Internal so the headless test drives the
    /// real window-ancestor lookup + the two index writes without synthesizing a pointer release.
    /// </summary>
    internal bool JumpToAccountsSettings()
    {
        if (this.FindAncestorOfType<Window>()?.DataContext is not MainViewModel main)
        {
            return false;
        }

        main.SelectedTabIndex = 2;                        // Settings tab (Uploads, Uploaded, Settings, Logs)
        main.SettingsViewModel.SelectedCategoryIndex = 3; // Accounts category (after General/Upload/Connection)
        return true;
    }

    // ── Editable Order cell (prep 7) ──

    /// <summary>Finds the Order column by its unique localized header text. Returns null if the header has not
    /// yet resolved (the guards re-resolve lazily).</summary>
    private DataGridColumn? ResolveOrderColumn()
    {
        string orderHeader = Localizer.Instance["Uploads_Col_Order"];
        return uploadsGrid.Columns.FirstOrDefault(c => Equals(c.Header, orderHeader));
    }

    private DataGridColumn? ResolveNameColumn()
    {
        string nameHeader = Localizer.Instance["Uploads_Col_Name"];
        return uploadsGrid.Columns.FirstOrDefault(c => Equals(c.Header, nameHeader));
    }

    /// <summary>
    /// Restricts editing to the Order cell on non-terminal file rows. The grid is editable (IsReadOnly=False)
    /// ONLY so the Order cell can be typed into; a grid-level IsReadOnly=True would force every column
    /// read-only and a column-level IsReadOnly=False cannot override it. So instead we cancel the edit for
    /// every other column, for package rows, and for terminal file rows (which show a blank order) before they
    /// enter edit mode. Mirrors the WPF <c>UploadsGrid_BeginningEdit</c> (UploadsView.xaml.cs:215-225); the
    /// WPF <c>e.Row.Item</c> is <c>e.Row.DataContext</c> on Avalonia's DataGridRow (no <c>Item</c> member).
    /// </summary>
    private void UploadsGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        => e.Cancel = ShouldCancelEdit(e.Column, e.Row.DataContext);

    /// <summary>The BeginningEdit guard: the grid has exactly two editable cells — the Order cell on a
    /// non-terminal FILE row, and the Name cell on a PACKAGE row (rename; a file row's Name is the real
    /// filename the hosters receive, never editable). Everything else cancels before entering edit mode.
    /// Internal so the headless test asserts the decision directly (constructing a real
    /// DataGridBeginningEditEventArgs and raising the private event is not feasible headlessly).</summary>
    internal bool ShouldCancelEdit(DataGridColumn? column, object? rowItem)
    {
        _orderColumn ??= ResolveOrderColumn();
        _nameColumn ??= ResolveNameColumn();

        if (column == _nameColumn)
        {
            return rowItem is not Package; // rename is a package-row affair
        }

        return column != _orderColumn
            || rowItem is not PackageFile file
            || file.State is FileState.Completed or FileState.Failed or FileState.Cancelled;
    }

    /// <summary>
    /// Commits an edited "Order" cell to a move. The editing TextBox holds the raw typed 1-based position;
    /// <see cref="UploadsViewModel.SetOrderCommand"/> routes it through the package manager, which clamps and
    /// re-numbers. Package and terminal rows are ignored. Mirrors the WPF <c>UploadsGrid_CellEditEnding</c>
    /// (UploadsView.xaml.cs:232-260). For the Order <c>DataGridTemplateColumn</c> the editing element IS the
    /// TextBox from the (Mode=OneWay) CellEditingTemplate, so no visual-tree walk is needed.
    /// </summary>
    private void UploadsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || DataContext is not UploadsViewModel vm)
        {
            return;
        }

        if (ResolveOrderEdit(e.Column, e.Row.DataContext, e.EditingElement) is { } order)
        {
            vm.SetOrderCommand.Execute(order);
        }
        else if (ResolveRenameEdit(e.Column, e.Row.DataContext, e.EditingElement) is { } rename)
        {
            vm.RenamePackageCommand.Execute(rename);
        }
    }

    /// <summary>
    /// The CellEditEnding commit computation for the Name cell: the <c>(package, trimmedName)</c> tuple to
    /// hand <see cref="UploadsViewModel.RenamePackageCommand"/>, or null when the column is not Name, the
    /// row is not a <see cref="Package"/>, the editing element is not a TextBox, or the trimmed text is
    /// blank/unchanged (blank must not wipe a name; unchanged skips a pointless persist round-trip).
    /// Internal so the headless test asserts the exact tuple.
    /// </summary>
    internal (Package Package, string Name)? ResolveRenameEdit(DataGridColumn? column, object? rowItem, Control? editingElement)
    {
        _nameColumn ??= ResolveNameColumn();
        if (column != _nameColumn || rowItem is not Package package)
        {
            return null;
        }

        string? trimmed = (editingElement as TextBox)?.Text?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == package.Name
            ? null
            : (package, trimmed);
    }

    /// <summary>
    /// The CellEditEnding commit computation: the <c>(file, target)</c> tuple to hand
    /// <see cref="UploadsViewModel.SetOrderCommand"/>, or null when the column is not Order, the row is not a
    /// <see cref="PackageFile"/>, the editing element is not a TextBox, or its text is not a valid int.
    /// Internal so the headless test asserts the exact tuple (the plan's requirement) without reaching the
    /// scheduler's async MoveFileTo.
    /// </summary>
    internal (PackageFile File, int Target)? ResolveOrderEdit(DataGridColumn? column, object? rowItem, Control? editingElement)
    {
        _orderColumn ??= ResolveOrderColumn();
        if (column != _orderColumn || rowItem is not PackageFile file)
        {
            return null;
        }

        return editingElement is TextBox tb && int.TryParse(tb.Text, out int target)
            ? (file, target)
            : null;
    }

    // ── Right-click target + context-menu suppression (rule 18, prep 8) ──

    private void UploadsGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(uploadsGrid).Properties.IsRightButtonPressed)
        {
            ApplyRightClickSelection(e.Source as Visual);
        }
    }

    /// <summary>
    /// Records whether a right-click landed on a package/file row (vs empty space below the rows), for
    /// <see cref="SnapshotSelectionAndDecideSuppression"/>. The ROW SELECTION itself is performed by the
    /// grid's <c>SelectRowOnRightClick</c> behavior (Task 10 — its first consumer, prep 8); this only captures
    /// the target because <c>ContextMenu.Opening</c> carries no pointer source. Internal + source-taking so the
    /// headless suppression test drives it directly (the sanctioned Phase 5 fallback to synthesizing a real
    /// pointer event on a specific row).
    /// </summary>
    internal void ApplyRightClickSelection(Visual? source)
        => _rightClickOnItem = source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: Package or PackageFile };

    private void RowContextMenu_Opening(object? sender, CancelEventArgs e)
        => e.Cancel = SnapshotSelectionAndDecideSuppression();

    /// <summary>
    /// Snapshots the full multi-selection into the VM (so the per-column Copy commands act on every selected
    /// row, not just the primary <see cref="UploadsViewModel.SelectedRow"/>) and returns whether the menu
    /// should be suppressed — true when the right-click landed on empty space, where every entry would be a
    /// useless no-op. Column headers never reach here (Task 3's AttachToHeaders handles them first with
    /// <c>e.Handled</c>). Internal so the headless suppression test asserts it without raising a real
    /// ContextRequested. Mirrors the WPF <c>UploadsGrid_ContextMenuOpening</c> (UploadsView.xaml.cs:324-351).
    /// </summary>
    internal bool SnapshotSelectionAndDecideSuppression()
    {
        if (DataContext is UploadsViewModel vm)
        {
            vm.SelectedRows = [.. uploadsGrid.SelectedItems.Cast<object>()];
        }

        // Rename targets the focused row and only makes sense for a package.
        RenamePackageMenuItem.IsEnabled = uploadsGrid.SelectedItem is Package;

        return !_rightClickOnItem;
    }

    /// <summary>
    /// Context menu "Rename Package…" — jumps straight into the Name cell's inline editor for the
    /// selected package row. Posted to the dispatcher so the edit begins AFTER the closing menu has
    /// released focus (BeginEdit during menu close loses the editor immediately).
    /// </summary>
    private void RenamePackage_Click(object? sender, RoutedEventArgs e)
        => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => TryBeginRenameForSelectedPackage());

    /// <summary>Moves the current cell to the Name column of the selected PACKAGE row and begins the
    /// inline edit; false when the selection isn't a package (the menu item is disabled then, so this is
    /// belt-and-braces). Internal so the headless test drives it directly.</summary>
    internal bool TryBeginRenameForSelectedPackage()
    {
        if (uploadsGrid.SelectedItem is not Package package)
        {
            return false;
        }

        _nameColumn ??= ResolveNameColumn();
        if (_nameColumn is null)
        {
            return false;
        }

        uploadsGrid.ScrollIntoView(package, _nameColumn);
        uploadsGrid.CurrentColumn = _nameColumn;
        return uploadsGrid.BeginEdit();
    }

    // ── Package-expanding copy (the DIVERGENCE from UploadedView) ──

    /// <summary>Ctrl+C / Ctrl+Insert intercept: runs the package-expanding copy and marks the event Handled so
    /// the DataGrid's built-in copy (which serializes only SelectedItems, no child expansion, and answers to
    /// both gestures) never runs.</summary>
    private void UploadsGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.C || e.Key == Key.Insert) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyRowsToClipboard();
            e.Handled = true;
        }
    }

    private void CopyRow_Click(object? sender, RoutedEventArgs e) => CopyRowsToClipboard();

    /// <summary>
    /// Copies the selected rows as TSV to the clipboard, expanding any selected Package to include its child
    /// files. Mirrors the WPF <c>OnCopyWithChildrenExecuted</c> (UploadsView.xaml.cs:368-424); the WPF
    /// <c>Clipboard.SetText</c> becomes an AWAITED <c>TopLevel.Clipboard.SetTextAsync</c> with the same
    /// swallow-on-failure guard (rule 9) — matching the ErrorDetailsWindow / ProxyTextDialog Copy handlers.
    /// </summary>
    private async void CopyRowsToClipboard()
    {
        if (BuildRowCopyText() is not { } text)
        {
            return;
        }

        try
        {
            // Await the write so this catch actually observes an (async) contention failure — the earlier
            // fire-and-forget shape returned the task unawaited, so the catch could never see it (rule 9).
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Clipboard writes can throw on rare contention with another app — swallow rather than crash the
            // UI thread for a copy operation.
        }
    }

    /// <summary>
    /// Builds the package-expanding TSV payload: for each selected row (de-duplicated), that row followed by
    /// its child files if it is a Package; then every VISIBLE column in display order, evaluated through its
    /// <see cref="DataGridColumn.ClipboardContentBinding"/> (converters/formatters honoured), tab-joined. Null
    /// when nothing is selected. Internal so the headless test asserts the exact string without the clipboard.
    /// </summary>
    internal string? BuildRowCopyText()
    {
        object[] selection = [.. uploadsGrid.SelectedItems.Cast<object>()];
        if (selection.Length == 0)
        {
            return null;
        }

        List<object> expanded = [];
        HashSet<object> seen = [];
        foreach (object item in selection)
        {
            if (!seen.Add(item))
            {
                continue;
            }

            expanded.Add(item);
            if (item is Package pkg)
            {
                foreach (PackageFile child in pkg)
                {
                    if (seen.Add(child))
                    {
                        expanded.Add(child);
                    }
                }
            }
        }

        DataGridColumn[] columns = [.. uploadsGrid.Columns
            .Where(c => c.IsVisible)
            .OrderBy(c => c.DisplayIndex)];

        StringBuilder sb = new();
        if (uploadsGrid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader)
        {
            sb.AppendLine(string.Join("\t", columns.Select(c => c.Header?.ToString() ?? string.Empty)));
        }

        foreach (object item in expanded)
        {
            sb.AppendLine(string.Join("\t", columns.Select(c => EvaluateClipboardBinding(c.ClipboardContentBinding, item))));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Evaluates a column's <see cref="DataGridColumn.ClipboardContentBinding"/> against a row item by routing
    /// it through a throwaway bound TextBlock (§Reality-check #8 — the binding approach, VERIFIED to resolve
    /// synchronously on 11.3.18; the VM's ColumnValueExtractor was the fallback, unneeded). The binding
    /// pipeline honours the same converters/StringFormat the grid's own copy uses, so this keeps parity
    /// without re-implementing them. Mirrors the WPF <c>EvaluateClipboardBinding</c> (UploadsView.xaml.cs:432-444).
    /// </summary>
    private static string EvaluateClipboardBinding(IBinding? binding, object item)
    {
        if (binding is null)
        {
            return string.Empty;
        }

        TextBlock tb = new() { DataContext = item };
        using IDisposable subscription = tb.Bind(TextBlock.TextProperty, binding);
        return tb.Text ?? string.Empty;
    }
}
