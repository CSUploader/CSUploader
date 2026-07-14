// <copyright file="DataGridDeleteKeyGuard.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System;
using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace CSUploader.Views;

/// <summary>
/// Builds the Delete <see cref="KeyBinding"/> for an EDITABLE DataGrid so the row-remove command fires ONLY
/// while no cell editor holds focus — restoring WPF's Delete-edits-text-while-editing semantics.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's <c>KeyboardDevice.ProcessRawEvent</c> walks the visual-ancestor <see cref="KeyBinding"/>s of the
/// FOCUSED element (the editing TextBox, when a cell is in edit) and calls <c>KeyBinding.TryHandle</c> BEFORE it
/// raises KeyDown to that focused element. <c>TryHandle</c> marks the KeyDown <c>Handled</c> — swallowing it
/// from the editor — ONLY when the bound command's <c>CanExecute</c>
/// returns <see langword="true"/> (verified via ILSpy on Avalonia.Base 11.3.18). So wrapping the remove command
/// in one whose CanExecute reports <see langword="false"/> while a cell editor is focused makes TryHandle
/// decline WITHOUT marking Handled, and the Delete keystroke falls through to the editing TextBox (WPF parity).
/// The plain read-only grids (accounts) have no cell editors, so their raw Delete binding is already correct
/// and is left untouched — this guard is only for grids whose <c>IsReadOnly=False</c> realizes a cell editor.
/// </para>
/// </remarks>
internal static class DataGridDeleteKeyGuard
{
    /// <summary>
    /// A Delete <see cref="KeyBinding"/> whose command is <paramref name="removeCommand"/> wrapped so its
    /// CanExecute is <see langword="false"/> while a TextBox cell editor inside <paramref name="grid"/> holds
    /// focus, otherwise delegating CanExecute/Execute to the inner command with <paramref name="selectedItems"/>
    /// as the parameter (rule 19 — the grid's live SelectedItems).
    /// </summary>
    public static KeyBinding CreateDeleteKeyBinding(DataGrid grid, IRelayCommand removeCommand, IList selectedItems)
        => new()
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = new EditorGuardedCommand(grid, removeCommand),
            CommandParameter = selectedItems,
        };

    /// <summary>
    /// Delegates to <see cref="Inner"/> but reports CanExecute=false whenever a cell editor TextBox inside the
    /// owning grid holds focus (see <see cref="DataGridDeleteKeyGuard"/> remarks for why this restores WPF
    /// Delete semantics). Internal so the headless regression tests can assert the delegation target and drive
    /// <see cref="IsCellEditorFocused"/> directly.
    /// </summary>
    internal sealed class EditorGuardedCommand(DataGrid grid, IRelayCommand inner) : IRelayCommand
    {
        /// <summary>The wrapped remove command the guard delegates to when no cell editor is focused.</summary>
        internal IRelayCommand Inner => inner;

        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => !IsCellEditorFocused() && inner.CanExecute(parameter);

        public void Execute(object? parameter) => inner.Execute(parameter);

        public void NotifyCanExecuteChanged() => inner.NotifyCanExecuteChanged();

        /// <summary>True when a TextBox cell editor that is a visual descendant of the owning grid holds focus —
        /// the only state where Delete must edit text instead of removing rows.</summary>
        internal bool IsCellEditorFocused()
            => TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement() is TextBox editor
               && grid.IsVisualAncestorOf(editor);
    }
}
