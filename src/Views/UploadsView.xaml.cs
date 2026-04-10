// <copyright file="UploadsView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class UploadsView : UserControl
{
    public UploadsView()
    {
        InitializeComponent();
    }

    private void OverviewCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UploadsViewModel vm)
        {
            vm.ShowUploadOverview = false;
        }
    }
}
