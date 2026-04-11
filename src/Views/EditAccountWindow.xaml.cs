// <copyright file="EditAccountWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Dal;
using CSUploader.Upload;

namespace CSUploader.Views;

public partial class EditAccountWindow : Window
{
    private readonly FileHosterLoginDto _original;

    public EditAccountWindow(FileHosterLoginDto account, string[] hosters)
    {
        InitializeComponent();

        _original = account;

        HosterCombo.ItemsSource = hosters;
        HosterCombo.SelectedItem = account.FileHosterName;
        HosterCombo.IsEnabled = account.Id == 0; // Lock hoster for existing accounts

        UsernameBox.Text = account.Username;
        PasswordBox.Text = account.Password;

        EnabledCheck.IsChecked = !account.Disabled;
    }

    public FileHosterLoginDto Result { get; private set; } = null!;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new FileHosterLoginDto
        {
            Id = _original.Id,
            FileHosterName = HosterCombo.SelectedItem as string ?? _original.FileHosterName,
            Username = UsernameBox.Text,
            Password = PasswordBox.Text,
            AccountType = _original.AccountType, // Preserved; auto-detected on check/refresh
            Disabled = EnabledCheck.IsChecked != true,
        };

        DialogResult = true;
    }
}
