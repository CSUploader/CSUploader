// <copyright file="SettingsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CSUploader.Dal;
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
/// Task 6 fills panel 3 — the READ-ONLY accounts grid. Its code-behind mirrors the proxy grid's
/// context-menu flip / SelectedItems / Delete plumbing, plus the double-tap-to-edit (rule 22) and the
/// enable/disable checkbox fan-out (the clicked row alone, or the whole selection when it is selected).
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// Accounts-grid row opacity: a Disabled account dims to 0.45 (WPF DataGridRow DataTrigger on Disabled).
    /// Bound per-row in XAML (the accounts grid's scoped DataGrid.Styles) via <c>{x:Static}</c>. A built-in
    /// <see cref="FuncValueConverter{TIn,TOut}"/>, not a new converter type — Core gains nothing this phase.
    /// </summary>
    public static readonly IValueConverter DisabledToOpacity =
        new FuncValueConverter<bool, double>(disabled => disabled ? 0.45 : 1.0);

    // True when the last right-button press on the proxy grid landed on a data row (vs empty space). The
    // ContextMenu.Opening handler reads it to hide the row-only items — Opening carries no pointer source,
    // so the decision is recorded here at press time (mirror of the WPF ProxyGrid_ContextMenuOpening walk).
    private bool _proxyRightClickOnItem;
    private bool _proxyDeleteWired;

    // The same row-vs-whitespace flag + Delete-wired guard for the accounts grid.
    private bool _accountRightClickOnItem;
    private bool _accountsDeleteWired;

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

        // ── Accounts grid (Task 6): same rule-19 SelectedItems wiring. The IList commands (Refresh / Enable /
        // Disable / Delete) and the bottom-bar Remove button take the grid's live SelectedItems; Edit has no
        // parameter (it operates on SelectedAccount). ──
        AccountsContextRefreshItem.CommandParameter = accountsGrid.SelectedItems;
        AccountsContextEnableItem.CommandParameter = accountsGrid.SelectedItems;
        AccountsContextDisableItem.CommandParameter = accountsGrid.SelectedItems;
        AccountsContextDeleteItem.CommandParameter = accountsGrid.SelectedItems;
        AccountsRemoveButton.CommandParameter = accountsGrid.SelectedItems;

        accountsGrid.AddHandler(InputElement.PointerPressedEvent, AccountsGrid_PointerPressed, RoutingStrategies.Tunnel);

        // The accounts grid inherits the SettingsView's own SettingsViewModel DataContext (set by the host
        // MainWindow), not present at construction — wire the Delete key when it resolves.
        accountsGrid.DataContextChanged += OnAccountsGridDataContextChanged;
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

    // ── Accounts grid (Task 6) ──

    /// <summary>The SettingsViewModel the accounts grid inherits (null until the view is attached).</summary>
    private SettingsViewModel? AccountsVm => accountsGrid.DataContext as SettingsViewModel;

    private void OnAccountsGridDataContextChanged(object? sender, EventArgs e)
    {
        if (_accountsDeleteWired || accountsGrid.DataContext is not SettingsViewModel vm)
        {
            return;
        }

        _accountsDeleteWired = true;

        // Rule 24: KeyBinding is a non-DataContext AvaloniaObject, so wire it in code-behind where the VM
        // command and the live SelectedItems are both in hand (parameter per rule 19).
        accountsGrid.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = vm.RemoveSelectedAccountsCommand,
            CommandParameter = accountsGrid.SelectedItems,
        });
    }

    private void AccountsGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(accountsGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        ApplyAccountsRightClickTarget(e.Source as Visual);
    }

    /// <summary>
    /// Records whether a right-click landed on a data row (vs empty space) for the context-menu flip.
    /// Internal + source-taking so the headless tests drive it directly (no synthesized pointer event).
    /// </summary>
    internal void ApplyAccountsRightClickTarget(Visual? source)
        => _accountRightClickOnItem = source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null;

    private void AccountsGrid_ContextMenuOpening(object? sender, CancelEventArgs e)
        => ApplyAccountsContextRowItemVisibility();

    /// <summary>
    /// Shows the row-only account items (Edit / Refresh / Enable / Disable / Delete + their separators) when
    /// the right-click landed on a row, hides them on empty space — the menu still opens with Add. Mirrors
    /// the WPF <c>AccountsGrid_ContextMenuOpening</c>. Returns the row/whitespace verdict; internal so the
    /// headless test can assert the flip without raising a real ContextRequested.
    /// </summary>
    internal bool ApplyAccountsContextRowItemVisibility()
    {
        bool onRow = _accountRightClickOnItem;
        AccountsContextEditItem.IsVisible = onRow;
        AccountsContextRefreshItem.IsVisible = onRow;
        AccountsContextRowSeparator1.IsVisible = onRow;
        AccountsContextEnableItem.IsVisible = onRow;
        AccountsContextDisableItem.IsVisible = onRow;
        AccountsContextRowSeparator2.IsVisible = onRow;
        AccountsContextDeleteItem.IsVisible = onRow;
        AccountsContextRowSeparator3.IsVisible = onRow;
        return onRow;
    }

    /// <summary>
    /// Double-tap opens the Phase-5 EditAccountWindow via <c>EditAccountCommand</c> (which operates on
    /// SelectedAccount — the tap has already selected the row). Port rule 22: only a tap on a data row opens
    /// it; a header / empty-area / scrollbar double-tap is ignored (walks up to a <see cref="DataGridRow"/>).
    /// </summary>
    private void AccountsGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SourceIsAccountRow(e.Source as Visual) && AccountsVm is { } vm && vm.EditAccountCommand.CanExecute(null))
        {
            vm.EditAccountCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// True when a pointer source lies within an account data row (a <see cref="DataGridRow"/> bound to a
    /// <see cref="FileHosterLoginDto"/>) — false for a header / empty-area / scrollbar tap. Internal static so
    /// the "double-tap on the header does NOT edit" case is testable without a synthesized pointer (mirrors
    /// the LogsView row-detection pattern).
    /// </summary>
    internal static bool SourceIsAccountRow(Visual? source)
        => source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: FileHosterLoginDto };

    /// <summary>
    /// Enable/disable checkbox fan-out (mirrors the WPF <c>AccountEnabledCheckBox_Click</c>): if the clicked
    /// row is part of the current multi-selection, the toggle fans out to every selected account; otherwise it
    /// targets the clicked row alone. Reading IsChecked here gives the post-toggle state (the CheckBox toggles
    /// before it raises Click). The checkbox reflects the INVERSE of Disabled, so unchecked ⇒ disable.
    /// </summary>
    private void AccountEnabledCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || checkBox.DataContext is not FileHosterLoginDto account
            || AccountsVm is not { } vm)
        {
            return;
        }

        bool disable = checkBox.IsChecked != true;

        List<FileHosterLoginDto> targets = EnableToggleTargets(account);

        ICommand command = disable ? vm.DisableSelectedAccountsCommand : vm.EnableSelectedAccountsCommand;
        command.Execute(targets);
    }

    /// <summary>
    /// The enable/disable fan-out target set for a checkbox click on <paramref name="clicked"/>: the whole
    /// current selection when the clicked row is part of it, otherwise the clicked row alone. Internal so the
    /// fan-out targeting is testable directly (without invoking the async enable/disable command + its reload).
    /// </summary>
    internal List<FileHosterLoginDto> EnableToggleTargets(FileHosterLoginDto clicked)
    {
        IList selected = accountsGrid.SelectedItems;
        return selected.Contains(clicked)
            ? [.. selected.OfType<FileHosterLoginDto>()]
            : [clicked];
    }
}
