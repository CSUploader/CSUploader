// <copyright file="UploadsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
/// Phase 5 helper. Context menu, editable Order, right-click select and the package-expanding copy are
/// Tasks 11-12 — the grid is left ready for them (IsReadOnly=False, the two selection behaviors on).
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

    public UploadsView()
    {
        InitializeComponent();

        // Rule 19: the SelectedItems-carrying command parameter is wired in code-behind. The grid's
        // SelectedItems is one live IList for the control's lifetime — the same instance the WPF
        // ElementName binding resolved to. (The Delete key + context menu land in Task 11.)
        RemoveButton.CommandParameter = uploadsGrid.SelectedItems;

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
    }

    private void ViewModel_FilterInvalidated(object? sender, EventArgs e) => _rowsView?.Refresh();

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

    // Add → Upload Wizard. Wired in Task 12; a no-op stub this task so the toolbar renders and the XAML
    // Click handler resolves.
    private void AddUploadButton_Click(object? sender, RoutedEventArgs e)
    {
    }
}
