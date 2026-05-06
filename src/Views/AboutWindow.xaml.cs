// <copyright file="AboutWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CSUploader.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        VersionText.Text = $"Version {version}";
    }

    /// <summary>
    /// Opens the URL stored on the clicked TextBlock's Tag in the default browser. Hit-tests
    /// to a Visual TextBlock rather than an inline Run, sidestepping WPF's hyperlink-navigation
    /// pipeline and the "Run is not a Visual or Visual3D" path it can hit on Win11.
    /// </summary>
    private void GithubLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not string url || string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; failing silently is fine for a link click.
        }

        e.Handled = true;
    }
}
