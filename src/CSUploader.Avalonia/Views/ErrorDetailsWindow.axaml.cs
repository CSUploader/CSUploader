// <copyright file="ErrorDetailsWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CSUploader.Views;

/// <summary>
/// Read-only modal that shows the full text of an error (the human-readable summary plus any raw
/// response body) with a Copy button — port of the WPF <c>ErrorDetailsWindow</c>. It exists so a verbose
/// sign-in failure, which can carry a multi-hundred-character HTML snippet from the XFileSharing pipeline,
/// need not be crammed into the fixed-size Add Account window that opens it.
/// </summary>
/// <remarks>
/// No <see cref="IDialogService"/> member: its production opener is the <see cref="EditAccountWindow"/>
/// "Details" link (Phase 5 Task 9, its first production consumer), which constructs it directly with the
/// full sign-in failure text; the dev gallery button constructs it the same way for the shot.
/// </remarks>
public partial class ErrorDetailsWindow : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses
    // the detail overload.
    public ErrorDetailsWindow()
        : this(string.Empty)
    {
    }

    public ErrorDetailsWindow(string detail)
    {
        InitializeComponent();
        DetailBox.Text = detail;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Window is a TopLevel, so its own Clipboard serves (port rule 9). Swallow failures like the
            // WPF original: the clipboard can be held by another app, and the text stays selectable here.
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(DetailBox.Text ?? string.Empty);
            }
        }
        catch
        {
            // A clipboard failure isn't worth interrupting the user with an error of its own.
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
