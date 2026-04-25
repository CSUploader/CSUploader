// <copyright file="AboutWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;
using System.Windows;

namespace CSUploader.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        DataContext = new
        {
            Title = "CSUploader",
            Version = $"Version {version}",
        };
    }
}
