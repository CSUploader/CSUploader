// <copyright file="UploadView.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows.Controls;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class UploadView : UserControl
{
    public UploadView()
    {
        InitializeComponent();
    }

    // WPF PasswordBox doesn't support binding for security reasons.
    // These event handlers bridge the password values to the ViewModel.
    private void PasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UploadViewModel vm)
        {
            vm.ArchivePassword = PasswordBox.Password;
        }
    }

    private void ConfirmPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UploadViewModel vm)
        {
            vm.ArchivePasswordConfirm = ConfirmPasswordBox.Password;
        }
    }
}
