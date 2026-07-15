// <copyright file="LogsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.Lib.UI;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// The Logs tab: an Auto-Scroll toggle over four read-only, single-select log grids (Status / HTTP / Errors /
/// UI). Avalonia port of the WPF <c>LogsView</c>. The four grids share the wiring done here in code-behind:
/// per-grid column-visibility persistence (Task 3 helper, keyed off each grid's <see cref="Control.Tag"/>),
/// and a details-open path — double-tap the clicked row (port rule 22) or Enter on the selected row through a
/// TUNNEL <see cref="InputElement.KeyDownEvent"/> (port rule 23, beating the grid's own Enter) — that opens
/// <see cref="HttpDetailsWindow"/> for entries with a captured transaction and <see cref="LogDetailsWindow"/>
/// otherwise. The Core <see cref="LogsViewModel"/> is untouched.
/// </summary>
public partial class LogsView : UserControl
{
    // XAML-default column state per grid, captured at first Loaded before any persisted overrides are
    // applied, so "Reset columns" can restore the shipped layout. Doubles as the once-only wire guard
    // (ContainsKey), mirroring the WPF head: Loaded fires again on every tab switch.
    private readonly Dictionary<DataGrid, Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState>> _defaultColumnState = [];

    // The column-toggle menu built per grid at first Loaded. Exposed (via ColumnMenuFor) so the headless
    // tests can assert each grid wired its menu with the first (anchor) column toggle disabled.
    private readonly Dictionary<DataGrid, ContextMenu> _columnMenus = [];

    public LogsView()
    {
        InitializeComponent();

        // The four grids exist after InitializeComponent (inline TabItem content), even though only the
        // selected tab's grid attaches to the visual tree; wire each one's handlers once here. The tunnel
        // KeyDown must be an AddHandler (RoutingStrategies carries no XAML form) and Loaded refires on tab
        // switches, so the wiring lives in code-behind rather than XAML.
        foreach (DataGrid grid in new[] { StatusLogGrid, HttpLogGrid, ErrorLogGrid, UILogGrid })
        {
            grid.DoubleTapped += LogGrid_DoubleTapped;
            grid.AddHandler(InputElement.KeyDownEvent, LogGrid_KeyDown, RoutingStrategies.Tunnel);
            grid.Loaded += LogGrid_Loaded;
        }
    }

    /// <summary>The details window the most recent double-tap/Enter opened, or null. Exposed so the headless
    /// tests can confirm the open path routed to the correct window type without a global window registry.</summary>
    internal Window? LastDetailsWindow { get; private set; }

    /// <summary>The column-toggle menu wired to <paramref name="grid"/> at its first Loaded, or null if the
    /// grid has not been wired yet (no SettingRepo, or its tab not shown). For the headless tests.</summary>
    internal ContextMenu? ColumnMenuFor(DataGrid grid) => _columnMenus.GetValueOrDefault(grid);

    // Details-open target for a double-tap: the clicked row's own entry (port rule 22 — open what was
    // clicked, not SelectedItem), or null when the tap landed on a header / empty area / scrollbar. Internal
    // static so the "double-tap on the header does NOT open" case is testable without a synthesized pointer.
    internal static LogEntryViewModel? RowEntryFromSource(object? source)
        => (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: LogEntryViewModel entry }
            ? entry
            : null;

    /// <summary>Opens the details window for one log entry: <see cref="HttpDetailsWindow"/> when the entry
    /// carries a captured transaction, <see cref="LogDetailsWindow"/> otherwise (mirrors the WPF
    /// <c>OpenDetails</c>). Shown modally over the host window when there is one. Internal so the tests can
    /// assert the window-type branch directly.</summary>
    internal Window OpenDetails(LogEntryViewModel entry)
    {
        Window window = entry.HasHttpTransaction
            ? new HttpDetailsWindow(entry)
            : new LogDetailsWindow(entry);

        LastDetailsWindow = window;

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        return window;
    }

    private void LogGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowEntryFromSource(e.Source) is { } entry)
        {
            OpenDetails(entry);
        }
    }

    private void LogGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter opens the selected row's details, mirroring a double-click. Handled during tunnelling so the
        // DataGrid's own Enter (move selection to the next row) doesn't run instead (port rule 23).
        if (e.Key == Key.Enter && sender is DataGrid grid && grid.SelectedItem is LogEntryViewModel entry)
        {
            OpenDetails(entry);
            e.Handled = true;
        }
    }

    private async void LogGrid_Loaded(object? sender, RoutedEventArgs e)
    {
        // Loaded refires on tab switches; wire each grid's columns only once. A repo is required for both the
        // Apply and the menu, so skip entirely without one (tests with no SettingRepo).
        if (sender is not DataGrid grid || grid.Tag is not string settingKey || _defaultColumnState.ContainsKey(grid)
            || DataContext is not LogsViewModel vm || vm.SettingRepo is not { } repo)
        {
            return;
        }

        // Capture XAML defaults *before* applying persisted overrides so "Reset columns" can restore them.
        _defaultColumnState[grid] = DataGridColumnVisibilityPersistence.CaptureCurrentState(grid);
        await DataGridColumnVisibilityPersistence.ApplyAsync(grid, repo, settingKey);

        ContextMenu menu = DataGridColumnMenu.Build(
            grid,
            _defaultColumnState[grid],
            repo,
            settingKey,
            vm.DialogServiceForView,
            "Logs_ResetColumns_Message",
            "Logs_ResetColumns_Title");
        _columnMenus[grid] = menu;
        DataGridColumnMenu.AttachToHeaders(grid, menu);

        // Persist column reorders the user does after the initial Apply.
        grid.ColumnDisplayIndexChanged += async (_, _) =>
            await DataGridColumnVisibilityPersistence.PersistAsync(grid, repo, settingKey);
    }
}
