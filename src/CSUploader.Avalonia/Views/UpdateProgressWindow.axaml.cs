// <copyright file="UpdateProgressWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;

namespace CSUploader.Views;

/// <summary>
/// Non-modal update-download progress window (port of the WPF <c>UpdateProgressWindow</c>). Owned and
/// driven by <see cref="Services.AvaloniaUpdateProgressSink"/>; <see cref="SetStatus"/> /
/// <see cref="SetProgress"/> are identical to the WPF port.
/// </summary>
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
