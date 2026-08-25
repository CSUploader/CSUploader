// <copyright file="UploadedView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.Lib.UI;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// The Uploaded tab: a grouped, read-only DataGrid over the completed-file rows. Avalonia port of the WPF
/// <c>UploadedView</c> — grouping is built in code-behind (Avalonia.Collections cannot live in Core, so the
/// VM keeps the raw <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> and the view wraps
/// it in a <see cref="DataGridCollectionView"/>). Right-click row/group targeting, the row context menu's
/// selection snapshot, the built-in copy, column-visibility persistence and the Delete key are all wired
/// here — the Core <see cref="UploadedViewModel"/> is untouched.
/// </summary>
public partial class UploadedView : UserControl
{
    // Snapshot of the XAML-default column state, captured at first Loaded before any persisted overrides
    // are applied; used by the column menu's "Reset columns" entry.
    private Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState>? _defaultColumnState;
    private UploadedViewModel? _vm;

    // The grouped view over the current VM's Files, built ONCE per VM and kept for its lifetime (rebuilding
    // it per reload was the leak — see OnDataContextChanged). Also the re-expand source on a Files Reset.
    private DataGridCollectionView? _view;

    // One view PER VM, weakly held: a DataContext bounce (VM A → B → back to A) must reuse A's view,
    // because DataGridCollectionView's ctor subscribes to the source's CollectionChanged and offers no
    // detach — minting a fresh view on each return would leave the abandoned one processing every later
    // Files mutation, the same leak the durable view fixed for reloads, arriving by another door. Weak,
    // so caching does not become the thing that keeps a dead VM alive.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<UploadedViewModel, DataGridCollectionView> _viewsByVm = [];
    private bool _columnsWired;
    private bool _deleteWired;

    // True when the last right-button press landed on a file row or a group header (vs empty space).
    // The ContextMenu.Opening handler reads it to suppress the menu on a whitespace right-click — Opening
    // carries no pointer source, so the decision is recorded here where the source is available.
    private bool _rightClickOnItem;

    // The selection the last right-click intended (the row, the preserved multi-selection, or a group's
    // rows). The tunnel press handler applies it immediately, but the DataGrid's OWN press handling runs
    // AFTER the tunnel phase and clears the selection again when the press landed on a group header —
    // observed as +3 then -3 in SelectionChanged — so the menu opened over an empty selection and Remove
    // no-oped. Opening re-applies this stash, which is ordering-proof: it runs after all press handling,
    // immediately before the menu shows.
    private IReadOnlyList<object>? _rightClickTargets;

    public UploadedView()
    {
        InitializeComponent();

        // Rule 19: the SelectedItems-carrying command parameters are wired in code-behind. The grid's
        // SelectedItems is one live IList for the control's lifetime — exactly what the WPF PlacementTarget
        // binding resolved to. (The commands themselves bind through the ContextMenu's inherited DataContext.)
        OpenUrlMenuItem.CommandParameter = FilesGrid.SelectedItems;
        RemoveMenuItem.CommandParameter = FilesGrid.SelectedItems;

        // Right-click selection (prep item 12): a TUNNEL handler so selection is updated before the row
        // ContextMenu's Opening snapshots it (the port of WPF's PreviewMouseRightButtonDown → ContextMenuOpening
        // ordering guarantee).
        FilesGrid.AddHandler(InputElement.PointerPressedEvent, FilesGrid_PointerPressed, RoutingStrategies.Tunnel);

        FilesGrid.Loaded += OnGridLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The internal, exposed for the headless tests to assert the header menu was attached with
    /// the first (anchor) column disabled.</summary>
    internal ContextMenu? ColumnMenu { get; private set; }

    /// <summary>Builds the grouped view Task 5 uses over the VM's raw collection: one path-group description
    /// over PackageName. Static + internal so the headless tests exercise the exact recipe.</summary>
    internal static DataGridCollectionView BuildGroupedView(System.Collections.IEnumerable files)
    {
        DataGridCollectionView view = new(files);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(UploadedFileRow.PackageName)));
        return view;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // A DataContext/VM swap (tab or window reuse) must not double-subscribe or leave the grid bound to
        // the previous VM's collection: drop the old VM's CollectionChanged hook and the old grouped view.
        if (_vm is not null)
        {
            _vm.Files.CollectionChanged -= OnFilesChanged;
            _vm.SearchInvalidated -= OnSearchInvalidated;
        }

        _view = null;

        _vm = DataContext as UploadedViewModel;
        if (_vm is null)
        {
            FilesGrid.ItemsSource = null;
            return;
        }

        // Build the grouped view ONCE and keep it for the VM's lifetime (assigned to the grid a single time).
        // Avalonia 11.3.13's DataGridCollectionView subscribes to the source's CollectionChanged in its ctor
        // and never removes that handler (it is not IDisposable), so the first port's habit of minting a fresh
        // view on every LoadAsync reload permanently orphaned one subscriber per reload on the long-lived
        // Files collection — unbounded growth plus an O(N²) regroup. One durable view fixes both — and the
        // per-VM cache extends "once" across DataContext bounces, which would otherwise re-mint it.
        _view = _viewsByVm.GetValue(_vm, static vm => BuildGroupedView(vm.Files));

        // The DURABLE view carries the search: the VM owns the text and the predicate, this view
        // applies it, and Files reloads flow through it filtered with no re-wiring. Same shape as
        // UploadsView's filter bar.
        _view.Filter = _vm.MatchesSearch;
        FilesGrid.ItemsSource = _view;
        _vm.Files.CollectionChanged += OnFilesChanged;
        _vm.SearchInvalidated += OnSearchInvalidated;
        WireDeleteKeyBinding(_vm);
    }

    /// <summary>
    /// Re-runs the durable view's filter for an edited search, with two decisions made rather than
    /// left to whatever the DataGrid does with a Reset:
    /// <para>
    /// Groups land EXPANDED — a search exists to reveal its matches, and a hit hidden under a
    /// group a user collapsed ten minutes ago is a search that looks broken. And selection is
    /// EXACTLY the surviving subset of what was selected, with the PRIMARY kept primary when it
    /// survives. The grid's own Reset handling prunes filtered-out rows but then PROMOTES some
    /// surviving row to SelectedItem — so without this, the current row (and with it keyboard
    /// range anchoring) silently jumps to a row the user did not make current; and a filtered-out
    /// primary must clear only itself, never the survivors beside it.
    /// </para>
    /// </summary>
    private void OnSearchInvalidated(object? sender, EventArgs e)
    {
        if (_view is null || _vm is null)
        {
            return;
        }

        // Primary counts only if it is in the SNAPSHOT. On this grid every observed state keeps
        // SelectedItem inside SelectedItems (a freshly shown grid arrives with row 0 genuinely
        // selected, and Clear() nulls both), so the gate is defensive - but the cost of the
        // assumption breaking is this handler SELECTING a row the user never picked, purely
        // because their search matched it, and that is not a risk worth one skipped Contains.
        object[] selected = [.. FilesGrid.SelectedItems.Cast<object>()];
        object? primary = FilesGrid.SelectedItem is { } current && selected.Contains(current) ? current : null;
        _view.Refresh();
        ExpandAllGroups();

        // Re-added with the surviving primary FIRST. Probed against this Avalonia rather than
        // assumed (the review and my first two fixes each guessed differently): SelectedItem
        // mirrors the grid's CURRENT row — Clear() resets it, the first Add after a Clear sets
        // it, and later Adds never move it. So after the Clear below, whichever row is re-added
        // first is the row that ends up current, and that must be the user's primary when it
        // survives.
        FilesGrid.SelectedItems.Clear();
        if (primary is not null && _vm.MatchesSearch(primary))
        {
            FilesGrid.SelectedItems.Add(primary);
        }

        foreach (object item in selected)
        {
            if (!ReferenceEquals(item, primary) && _vm.MatchesSearch(item))
            {
                FilesGrid.SelectedItems.Add(item);
            }
        }
    }

    private void ExpandAllGroups()
    {
        if (_view?.Groups is null)
        {
            return;
        }

        foreach (DataGridCollectionViewGroup group in _view.Groups.OfType<DataGridCollectionViewGroup>())
        {
            FilesGrid.ExpandRowGroup(group, expandAllSubgroups: true);
        }
    }

    // LoadAsync reloads by clearing then re-adding on the SAME Files collection. The durable grouped view
    // refreshes itself (it is subscribed to Files), and because Files.Clear() removes every group before the
    // re-adds recreate them, the DataGrid builds each recreated group with a fresh DataGridRowGroupInfo whose
    // IsVisible defaults to true — so a reload already lands with every group expanded (verified by the
    // Rebuild_ReExpandsAllGroups keystone). This handler re-expands in place as a guard for any Reset that
    // instead arrives with groups already populated, holding the WPF CollectionViewSource parity WITHOUT
    // minting a new view (the old rebuild here was the leak).
    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        ExpandAllGroups();
    }

    private void WireDeleteKeyBinding(UploadedViewModel vm)
    {
        if (_deleteWired)
        {
            return;
        }

        _deleteWired = true;

        // Rule 24: KeyBinding is a non-DataContext AvaloniaObject on 11.3.18, so wire it in code-behind
        // where the VM command and the live SelectedItems are both in hand (parameter per rule 19).
        FilesGrid.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = vm.RemoveSelectedCommand,
            CommandParameter = FilesGrid.SelectedItems,
        });
    }

    private async void OnGridLoaded(object? sender, RoutedEventArgs e)
    {
        // Loaded can fire more than once (tab switches re-attach the control); wire columns only once.
        if (_columnsWired || DataContext is not UploadedViewModel vm || vm.SettingRepo is not { } repo)
        {
            return;
        }

        _columnsWired = true;

        // Capture XAML defaults *before* applying persisted overrides so "Reset columns" can restore them.
        _defaultColumnState = DataGridColumnVisibilityPersistence.CaptureCurrentState(FilesGrid);
        await DataGridColumnVisibilityPersistence.ApplyAsync(FilesGrid, repo, SettingKey.UploadedTabHiddenColumns);

        ColumnMenu = DataGridColumnMenu.Build(
            FilesGrid,
            _defaultColumnState,
            repo,
            SettingKey.UploadedTabHiddenColumns,
            vm.DialogServiceForView,
            "Uploaded_ResetColumns_Message",
            "Uploaded_ResetColumns_Title");
        DataGridColumnMenu.AttachToHeaders(FilesGrid, ColumnMenu);

        // Persist column reorders the user does after the initial Apply.
        FilesGrid.ColumnDisplayIndexChanged += async (_, _) =>
            await DataGridColumnVisibilityPersistence.PersistAsync(FilesGrid, repo, SettingKey.UploadedTabHiddenColumns);
    }

    // Row copy = the DataGrid's built-in IncludeHeader clipboard path. A synthetic Ctrl+C reaches
    // ProcessCopyKey (Task 2 verdict #4), copying the header row + each selected row's ClipboardContentBinding
    // values — no code-behind text assembly needed.
    private void CopyRow_Click(object? sender, RoutedEventArgs e)
        => FilesGrid.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
        });

    private void UrlText_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Rule 10 button guard: only the left button opens the link.
        if (e.InitialPressMouseButton != MouseButton.Left
            || sender is not TextBlock tb
            || string.IsNullOrEmpty(tb.Text))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(tb.Text) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // Best-effort; failing silently is fine for a link click.
        }
    }

    private void FilesGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FilesGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        ApplyRightClickSelection(e.Source as Visual);
    }

    /// <summary>
    /// Selects the row (or every row in a group) under a right-click so the context menu acts on that
    /// target, preserving an existing multi-selection when the row is already part of it. Records whether the
    /// press landed on a row/group (vs empty space) for <see cref="SnapshotSelectionAndDecideSuppression"/>.
    /// Internal + source-taking so the headless tests drive it directly (the sanctioned fallback to
    /// synthesizing a real pointer event on a specific row).
    /// </summary>
    internal void ApplyRightClickSelection(Visual? source)
    {
        if (source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: UploadedFileRow item })
        {
            if (!FilesGrid.SelectedItems.Contains(item))
            {
                FilesGrid.SelectedItems.Clear();
                FilesGrid.SelectedItems.Add(item);
            }

            _rightClickTargets = [.. FilesGrid.SelectedItems.Cast<object>()];
            _rightClickOnItem = true;
            return;
        }

        // Right-clicked a group header (the package bar) — select every row in the group so Copy /
        // Open URL / Remove / Export act on the whole package (Task 2 checklist-5 route).
        if (source?.FindAncestorOfType<DataGridRowGroupHeader>(includeSelf: true) is { DataContext: DataGridCollectionViewGroup group })
        {
            FilesGrid.SelectedItems.Clear();
            foreach (object groupItem in group.Items)
            {
                FilesGrid.SelectedItems.Add(groupItem);
            }

            _rightClickTargets = [.. group.Items.Cast<object>()];
            _rightClickOnItem = true;
            return;
        }

        _rightClickTargets = null;
        _rightClickOnItem = false;
    }

    private void RowContextMenu_Opening(object? sender, CancelEventArgs e)
        => e.Cancel = SnapshotSelectionAndDecideSuppression();

    /// <summary>
    /// Snapshots the full multi-selection into the VM (so the per-column Copy commands act on every selected
    /// row, not just the primary <see cref="UploadedViewModel.SelectedRow"/>) and returns whether the menu
    /// should be suppressed — true when the right-click landed on empty space below the rows, where every
    /// entry would be a useless no-op. Internal so the headless suppression test can assert it without
    /// raising a real ContextRequested.
    /// </summary>
    internal bool SnapshotSelectionAndDecideSuppression()
    {
        // Re-apply the press's intended targets if anything (the DataGrid's own group-header press
        // handling) cleared them between the tunnel press and the menu opening. No-op when the selection
        // already matches — the common row case.
        if (_rightClickOnItem && _rightClickTargets is { Count: > 0 } targets
            && !targets.SequenceEqual(FilesGrid.SelectedItems.Cast<object>()))
        {
            FilesGrid.SelectedItems.Clear();
            foreach (object target in targets)
            {
                FilesGrid.SelectedItems.Add(target);
            }
        }

        if (DataContext is UploadedViewModel vm)
        {
            vm.SelectedRows = [.. FilesGrid.SelectedItems.OfType<UploadedFileRow>()];
        }

        return !_rightClickOnItem;
    }
}
