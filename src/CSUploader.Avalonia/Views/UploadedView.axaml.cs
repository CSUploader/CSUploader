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
    private bool _columnsWired;
    private bool _deleteWired;

    // True when the last right-button press landed on a file row or a group header (vs empty space).
    // The ContextMenu.Opening handler reads it to suppress the menu on a whitespace right-click — Opening
    // carries no pointer source, so the decision is recorded here where the source is available.
    private bool _rightClickOnItem;

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
        if (_vm is not null)
        {
            _vm.Files.CollectionChanged -= OnFilesChanged;
        }

        _vm = DataContext as UploadedViewModel;
        if (_vm is null)
        {
            return;
        }

        FilesGrid.ItemsSource = BuildGroupedView(_vm.Files);
        _vm.Files.CollectionChanged += OnFilesChanged;
        WireDeleteKeyBinding(_vm);
    }

    // LoadAsync clears then re-adds Files; rebuild the grouped view on the Reset so all groups re-expand —
    // the WPF CollectionViewSource parity the Task 2 probe verified (a fresh grouping defaults to expanded).
    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _vm is not null)
        {
            FilesGrid.ItemsSource = BuildGroupedView(_vm.Files);
        }
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

            _rightClickOnItem = true;
            return;
        }

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
        if (DataContext is UploadedViewModel vm)
        {
            vm.SelectedRows = [.. FilesGrid.SelectedItems.OfType<UploadedFileRow>()];
        }

        return !_rightClickOnItem;
    }
}
