// <copyright file="CloseActionDialog.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Upload;

namespace CSUploader.Views;

/// <summary>
/// First-run prompt for the main window's X (close) button (port of the WPF <c>CloseActionDialog</c>).
/// Lets the user pick between minimising to tray and exiting, with a "Remember my choice" checkbox so the
/// next click either repeats or re-prompts.
/// </summary>
/// <remarks>
/// Result via <c>ShowDialog&lt;CloseActionChoice?&gt;</c>: Minimize/Exit → a choice carrying the action and
/// the remember flag; Cancel/Esc/window-X → <c>null</c> (keep the window open, leave the setting
/// unchanged). Owner resolution is NOT done in the ctor (the WPF port resolved it inline) — the Phase 7
/// close-to-tray caller passes the owner to <c>ShowDialog</c>; the gallery passes itself.
/// </remarks>
public partial class CloseActionDialog : Window
{
    public CloseActionDialog()
    {
        InitializeComponent();
    }

    private void MinimizeToTray_Click(object? sender, RoutedEventArgs e)
        => Close(new CloseActionChoice(CloseAction.MinimizeToTray, RememberCheck.IsChecked == true));

    private void Exit_Click(object? sender, RoutedEventArgs e)
        => Close(new CloseActionChoice(CloseAction.Exit, RememberCheck.IsChecked == true));

    // Cancel/Esc → null: keep the window open, setting unchanged. WPF's Cancel set DialogResult=false;
    // Avalonia's IsCancel only routes Esc to Click without closing (port rule 7), so close explicitly.
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}

/// <summary>
/// The user's close-button choice: which <see cref="CloseAction"/> to take and whether to persist it
/// ("Remember my choice"). Carried through <c>ShowDialog&lt;CloseActionChoice?&gt;</c> — a <c>null</c>
/// result means the user cancelled (keep the window open, leave <c>AppSettings.CloseAction</c> at
/// <see cref="CloseAction.Ask"/>). Wired to MainWindow's close handling in Phase 7.
/// </summary>
internal readonly record struct CloseActionChoice(CloseAction Action, bool Remember);
