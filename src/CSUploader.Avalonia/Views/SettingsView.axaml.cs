// <copyright file="SettingsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// The Settings tab: a ListBox sidebar over four SelectedCategoryIndex-switched panels. Avalonia port of
/// the WPF <c>SettingsView</c>. Task 4 landed the shell + the General and Upload panels (bound directly to
/// <see cref="ViewModels.SettingsViewModel"/>); Task 5 fills the Connection (proxies) panel — an EDITABLE
/// grid whose DataContext is the <see cref="ConnectionManagerViewModel"/> reached via the parent Window's
/// MainViewModel. This code-behind carries only what the ported XAML can't express: the proxy grid's
/// row-vs-whitespace context-menu flip (recorded at right-click press time, rule 18), the SelectedItems
/// command parameters (rule 19), the Delete key binding (rule 24), and the split-button flyout actions.
/// The Accounts grid fills panel 3 in Task 6.
/// </summary>
public partial class SettingsView : UserControl
{
    // True when the last right-button press on the proxy grid landed on a data row (vs empty space). The
    // ContextMenu.Opening handler reads it to hide the row-only items — Opening carries no pointer source,
    // so the decision is recorded here at press time (mirror of the WPF ProxyGrid_ContextMenuOpening walk).
    private bool _proxyRightClickOnItem;
    private bool _proxyDeleteWired;

    public SettingsView()
    {
        InitializeComponent();

        // Rule 19: the SelectedItems-carrying context-menu commands take the grid's live SelectedItems as
        // their parameter (one IList for the control's lifetime — what the WPF PlacementTarget binding
        // resolved to). The commands themselves bind through the ContextMenu's inherited VM DataContext.
        ProxyContextTestItem.CommandParameter = ProxyGrid.SelectedItems;
        ProxyContextRemoveItem.CommandParameter = ProxyGrid.SelectedItems;
        ProxyContextExportSelectedToTextItem.CommandParameter = ProxyGrid.SelectedItems;
        ProxyContextExportSelectedToFileItem.CommandParameter = ProxyGrid.SelectedItems;

        // Record row-vs-whitespace at press time (tunnel, so it beats the menu's Opening).
        ProxyGrid.AddHandler(InputElement.PointerPressedEvent, ProxyGrid_PointerPressed, RoutingStrategies.Tunnel);

        // The proxy grid's VM (ConnectionManagerViewModel) arrives via the Window-ancestor binding once the
        // panel is attached, not at construction — wire the Delete key when the DataContext resolves.
        ProxyGrid.DataContextChanged += OnProxyGridDataContextChanged;
    }

    /// <summary>The proxy grid's VM, resolved from its Window-ancestor-bound DataContext (null until attached).</summary>
    private ConnectionManagerViewModel? ConnVm => ProxyGrid.DataContext as ConnectionManagerViewModel;

    private void OnProxyGridDataContextChanged(object? sender, EventArgs e)
    {
        if (_proxyDeleteWired || ProxyGrid.DataContext is not ConnectionManagerViewModel vm)
        {
            return;
        }

        _proxyDeleteWired = true;

        // Rule 24: KeyBinding is a non-DataContext AvaloniaObject, so wire it in code-behind where the VM
        // command and the live SelectedItems are both in hand (parameter per rule 19).
        ProxyGrid.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = vm.RemoveSelectedCommand,
            CommandParameter = ProxyGrid.SelectedItems,
        });
    }

    private void ProxyGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProxyGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        ApplyProxyRightClickTarget(e.Source as Visual);
    }

    /// <summary>
    /// Records whether a right-click landed on a data row (vs empty space) for the context-menu flip.
    /// Internal + source-taking so the headless tests drive it directly (no synthesized pointer event).
    /// </summary>
    internal void ApplyProxyRightClickTarget(Visual? source)
        => _proxyRightClickOnItem = source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null;

    private void ProxyGrid_ContextMenuOpening(object? sender, CancelEventArgs e)
        => ApplyProxyContextRowItemVisibility();

    /// <summary>
    /// Shows the row-only menu items (Test / Remove / Export-selected + their separators) when the
    /// right-click landed on a row, hides them on empty space — the menu itself always opens (Add / Import /
    /// Export stay). Mirrors the WPF <c>ProxyGrid_ContextMenuOpening</c>. Returns the row/whitespace verdict;
    /// internal so the headless test can assert the flip without raising a real ContextRequested.
    /// </summary>
    internal bool ApplyProxyContextRowItemVisibility()
    {
        bool onRow = _proxyRightClickOnItem;
        ProxyContextTestItem.IsVisible = onRow;
        ProxyContextRemoveItem.IsVisible = onRow;
        ProxyContextRowSeparator.IsVisible = onRow;
        ProxyContextExportSelectedSeparator.IsVisible = onRow;
        ProxyContextExportSelectedToTextItem.IsVisible = onRow;
        ProxyContextExportSelectedToFileItem.IsVisible = onRow;
        return onRow;
    }

    // Split-button flyout actions (rule 19 reason as the grid menu: the flyout lives in its own popup
    // namescope, so the VM + live SelectedItems are resolved in code-behind rather than bound across it).
    private void ProxyImportFromText_Click(object? sender, RoutedEventArgs e) => ConnVm?.ImportFromTextCommand.Execute(null);

    private void ProxyImportFromFile_Click(object? sender, RoutedEventArgs e) => ConnVm?.ImportFromFileCommand.Execute(null);

    private void ProxyExportAllToText_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportAllToTextCommand.Execute(null);

    private void ProxyExportAllToFile_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportAllToFileCommand.Execute(null);

    private void ProxyExportOkToText_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportOkToTextCommand.Execute(null);

    private void ProxyExportOkToFile_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportOkToFileCommand.Execute(null);

    private void ProxyExportSelectedToText_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportSelectedToTextCommand.Execute(ProxyGrid.SelectedItems);

    private void ProxyExportSelectedToFile_Click(object? sender, RoutedEventArgs e) => ConnVm?.ExportSelectedToFileCommand.Execute(ProxyGrid.SelectedItems);

    private void ProxyRemoveSelected_Click(object? sender, RoutedEventArgs e) => ConnVm?.RemoveSelectedCommand.Execute(ProxyGrid.SelectedItems);

    private void ProxyRemoveFailed_Click(object? sender, RoutedEventArgs e) => ConnVm?.RemoveFailedCommand.Execute(null);
}
