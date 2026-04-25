// <copyright file="SpeedLimitDialog.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;

namespace CSUploader.Views;

public partial class SpeedLimitDialog : Window
{
    public SpeedLimitDialog(int? currentLimit)
    {
        InitializeComponent();
        LimitBox.Text = currentLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        LimitBox.Focus();
        LimitBox.SelectAll();
    }

    /// <summary>
    /// The resulting limit in KB/s, or null if cleared / using the global.
    /// </summary>
    public int? Result { get; private set; }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string text = LimitBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Result = null;
            DialogResult = true;
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit <= 0)
        {
            MessageBox.Show("Please enter a positive integer (KB/s), or leave empty to clear.",
                "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = limit;
        DialogResult = true;
    }
}
