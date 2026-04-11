// <copyright file="UploadView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CSUploader.Views;

public partial class UploadView : UserControl
{
    public UploadView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Forwards mouse wheel events from the DataGrid to the parent ScrollViewer
    /// so the page scrolls when hovering over the file hosters list.
    /// </summary>
    private void FileHostersGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        // Find the parent ScrollViewer and forward the scroll event to it
        DependencyObject? parent = VisualTreeHelper.GetParent((DependencyObject)sender);
        while (parent is not null and not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        }
    }
}
