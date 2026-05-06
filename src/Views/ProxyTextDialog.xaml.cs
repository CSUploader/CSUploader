// <copyright file="ProxyTextDialog.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Views;

/// <summary>
/// Dual-purpose modal for the Connection Manager's text-based import/export. In editable
/// mode it gathers user-typed proxy lines (OK / Cancel); in read-only mode it shows the
/// formatted export with a Copy button so the user can lift the text into another tool.
/// </summary>
public partial class ProxyTextDialog : Window
{
    public ProxyTextDialog(string title, string description, string initialText, bool readOnly)
    {
        InitializeComponent();
        Title = title;
        DescriptionText.Text = description;
        BodyBox.Text = initialText;
        BodyBox.IsReadOnly = readOnly;

        if (readOnly)
        {
            // Export mode: hide the OK ("Import") button, show Copy, swap Cancel → Close.
            OkButton.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Visible;
            CancelButton.Content = "Close";
        }
    }

    /// <summary>
    /// Text the user typed/edited; only meaningful when <see cref="Window.DialogResult"/>
    /// is true (i.e. the user clicked Import).
    /// </summary>
    public string ResultText { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = BodyBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BodyBox.Text);
        }
        catch
        {
            // Clipboard can fail when another app holds it open; not worth interrupting
            // the user — the text is selectable in the box.
        }
    }
}
