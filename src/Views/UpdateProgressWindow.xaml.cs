// <copyright file="UpdateProgressWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;

namespace CSUploader.Views;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetProgress(int percent)
    {
        Progress.Value = percent;
        PercentText.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
    }
}
