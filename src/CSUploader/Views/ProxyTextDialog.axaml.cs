// <copyright file="ProxyTextDialog.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;

namespace CSUploader.Views;

/// <summary>
/// Dual-purpose modal for the Connection Manager's text-based import/export (port of the WPF
/// <c>ProxyTextDialog</c>). In editable mode it gathers user-typed proxy lines (Import / Cancel); in
/// read-only mode it shows the formatted export with a Copy button. The result is delivered through
/// <c>ShowDialog&lt;string?&gt;</c> — Import → the edited text, Cancel/Esc/window-X → <c>null</c> —
/// collapsing the WPF <c>DialogResult</c> + <c>ResultText</c> pair into a single value.
/// </summary>
public partial class ProxyTextDialog : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses
    // the four-arg overload.
    public ProxyTextDialog()
        : this(string.Empty, string.Empty, string.Empty, readOnly: false)
    {
    }

    public ProxyTextDialog(string title, string description, string initialText, bool readOnly)
    {
        InitializeComponent();
        Title = title;
        DescriptionText.Text = description;
        BodyBox.Text = initialText;
        BodyBox.IsReadOnly = readOnly;

        if (readOnly)
        {
            // Export mode: hide Import, show Copy, relabel Cancel → Close. WPF hardcoded the literal
            // "Close" (ProxyTextDialog.xaml.cs:30); the port uses the existing Common_Close key instead
            // (English-identical, and now localized) — a deliberate, strictly-better deviation.
            OkButton.IsVisible = false;
            CopyButton.IsVisible = true;
            CancelButton.Content = Localizer.Instance["Common_Close"];
        }
    }

    // Import → the edited text; Cancel/Esc → null. Avalonia's IsDefault/IsCancel route Enter/Esc to a
    // button's Click but do NOT auto-close the window (port rule 7), so each handler closes explicitly.
    // The window-X path runs no handler, so ShowDialog<string?> yields null (the WPF DialogResult != true
    // path) — read-only mode always ends up null too (Import is hidden), which the caller ignores.
    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(BodyBox.Text);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Window is a TopLevel, so its own Clipboard serves (port rule 9); swallow failures like the
            // WPF original — the clipboard can be held by another app and the text stays selectable.
            if (Clipboard is not null)
            {
                await Clipboard.SetValueAsync(global::Avalonia.Input.DataFormat.Text, BodyBox.Text ?? string.Empty);
            }
        }
        catch
        {
            // Not worth interrupting the user over a transient clipboard failure.
        }
    }
}
