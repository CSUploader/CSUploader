// <copyright file="SplashWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;

namespace CSUploader.Views;

/// <summary>
/// The "checking for updates" splash. Deliberately has no logic of its own: the startup sequence
/// drives it from <c>App</c>, and a window that decided anything for itself would be a second place
/// the transition could go wrong.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>Replaces the status line, for a step that outlives "checking".</summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
        Avalonia.Automation.AutomationProperties.SetName(StatusText, status);
    }
}
