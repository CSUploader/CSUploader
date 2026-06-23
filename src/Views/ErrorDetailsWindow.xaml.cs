// <copyright file="ErrorDetailsWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Views;

/// <summary>
/// Read-only modal that shows the full text of an error (the human-readable summary plus any
/// raw response body) with a Copy button. The Add Account window's "Details" link opens it so
/// a verbose sign-in failure — which can carry a multi-hundred-character HTML snippet from the
/// XFileSharing pipeline — doesn't have to be crammed into that fixed-size dialog.
/// </summary>
public partial class ErrorDetailsWindow : Window
{
    public ErrorDetailsWindow(string detail)
    {
        InitializeComponent();
        DetailBox.Text = detail;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailBox.Text);
        }
        catch
        {
            // Clipboard can fail when another app holds it open; the text stays selectable in
            // the box, so it isn't worth interrupting the user with an error of its own.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
