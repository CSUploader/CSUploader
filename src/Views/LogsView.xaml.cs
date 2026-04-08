// <copyright file="LogsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is LogEntryViewModel entry)
        {
            var window = new LogDetailsWindow(entry);
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
        }
    }
}
