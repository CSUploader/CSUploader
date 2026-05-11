// <copyright file="EditAccountWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Dal;
using CSUploader.Lib.Localization;

namespace CSUploader.Views;

public partial class EditAccountWindow : Window
{
    private readonly FileHosterLoginDto _original;

    public EditAccountWindow(FileHosterLoginDto account, string[] hosters)
    {
        InitializeComponent();

        _original = account;

        if (account.Id == 0)
        {
            HosterCombo.ItemsSource = hosters;
            HosterCombo.SelectedItem = account.FileHosterName;
        }
        else
        {
            // Lock hoster for existing accounts: show as read-only text
            HosterCombo.Visibility = Visibility.Collapsed;
            HosterLocked.Visibility = Visibility.Visible;
            HosterLockedText.Text = account.FileHosterName;
        }

        UsernameBox.Text = account.Username;
        PasswordBox.Text = account.Password;

        EnabledCheck.IsChecked = !account.Disabled;
    }

    public FileHosterLoginDto? Result { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text?.Trim() ?? string.Empty;
        string password = PasswordBox.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show(
                this,
                Localizer.Instance["EditAccount_Validation_RequireUsernameAndPassword"],
                Localizer.Instance["Common_Error"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            (string.IsNullOrWhiteSpace(username) ? UsernameBox : PasswordBox).Focus();
            return;
        }

        Result = new FileHosterLoginDto
        {
            Id = _original.Id,
            FileHosterName = HosterCombo.SelectedItem as string ?? _original.FileHosterName,
            Username = username,
            Password = password,
            AccountType = _original.AccountType, // Preserved; auto-detected on check/refresh
            Disabled = EnabledCheck.IsChecked != true,
        };

        DialogResult = true;
    }
}
