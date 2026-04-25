// <copyright file="LogDetailsWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class LogDetailsWindow : Window
{
    public LogDetailsWindow(LogEntryViewModel logEntry)
    {
        InitializeComponent();

        DataContext = logEntry;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
