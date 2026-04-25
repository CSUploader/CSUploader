// <copyright file="ConfirmationDialog.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(string message, string title)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    /// <summary>
    /// True when the user clicked Yes. False for No/close.
    /// </summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// True if the user ticked "Don't ask me again" before confirming.
    /// Only meaningful when <see cref="Confirmed"/> is true.
    /// </summary>
    public bool DontAskAgain => DontAskAgainCheck.IsChecked == true;

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}
