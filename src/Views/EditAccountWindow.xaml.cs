// <copyright file="EditAccountWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib.Localization;

// Avoid `using System.Windows.Controls;` — that namespace also defines a PasswordBox type
// which would shadow our XAML field named PasswordBox (we use a TextBox in the XAML, not
// a PasswordBox control). Fully-qualifying TextChangedEventArgs keeps the field accesses
// (PasswordBox.Text, etc.) resolving to the InitializeComponent-generated fields.

namespace CSUploader.Views;

public partial class EditAccountWindow : Window
{
    /// <summary>
    /// Hoster names whose pipeline accepts an API key as an alternative credential to
    /// username/password. The Add/Edit dialog exposes the ApiKey textbox only for these
    /// hosters and treats the two credential modes as mutually exclusive.
    /// </summary>
    private static readonly HashSet<string> HostersWithApiKeyMode =
        new(System.StringComparer.OrdinalIgnoreCase) { "ExLoad" };

    private readonly FileHosterLoginDto _original;
    private bool _suppressMutualExclusion;

    public EditAccountWindow(FileHosterLoginDto account, string[] hosters)
    {
        InitializeComponent();

        _original = account;

        if (account.Id == 0)
        {
            HosterCombo.ItemsSource = hosters;
            HosterCombo.SelectedItem = account.FileHosterName;
            HosterCombo.SelectionChanged += (_, _) => RefreshApiKeyVisibility();
        }
        else
        {
            // Lock hoster for existing accounts: show as read-only text + icon, mirroring
            // the ComboBox's templated row so the locked alternative doesn't look bare.
            HosterCombo.Visibility = Visibility.Collapsed;
            HosterLocked.Visibility = Visibility.Visible;
            HosterLockedText.Text = account.FileHosterName;
            HosterLockedIcon.Source = new HosterIconConverter()
                .Convert(account.FileHosterName ?? string.Empty, typeof(BitmapImage), null!, CultureInfo.CurrentCulture) as BitmapImage;
        }

        UsernameBox.Text = account.Username;
        PasswordBox.Text = account.Password;
        ApiKeyBox.Text = account.ApiKey;

        EnabledCheck.IsChecked = !account.Disabled;

        // Wire the mutually-exclusive grey-out: typing in U/P clears + disables ApiKey
        // (and vice versa). Done via TextChanged rather than data-bound IsEnabled so we
        // can also clear the opposite field on first keystroke, matching the user's
        // mental model that the two modes are alternatives.
        UsernameBox.TextChanged += OnCredentialFieldChanged;
        PasswordBox.TextChanged += OnCredentialFieldChanged;
        ApiKeyBox.TextChanged += OnApiKeyChanged;

        RefreshApiKeyVisibility();
        RefreshMutualExclusion();
    }

    public FileHosterLoginDto? Result { get; private set; }

    private void RefreshApiKeyVisibility()
    {
        string? hoster = HosterCombo.Visibility == Visibility.Visible
            ? HosterCombo.SelectedItem as string
            : _original.FileHosterName;

        bool supportsApiKey = hoster is not null && HostersWithApiKeyMode.Contains(hoster);

        Visibility v = supportsApiKey ? Visibility.Visible : Visibility.Collapsed;
        OrSeparator.Visibility = v;
        ApiKeyLabel.Visibility = v;
        ApiKeyBox.Visibility = v;
    }

    private void OnCredentialFieldChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressMutualExclusion)
        {
            return;
        }

        // If the user typed into Username or Password, the API-key mode is no longer in
        // play — clear and disable it. The clear matches the user's intent: their fresh
        // keystroke says "I'm using U/P now, throw away anything in ApiKey."
        if (!string.IsNullOrWhiteSpace(UsernameBox.Text) || !string.IsNullOrWhiteSpace(PasswordBox.Text))
        {
            if (!string.IsNullOrEmpty(ApiKeyBox.Text))
            {
                _suppressMutualExclusion = true;
                try { ApiKeyBox.Clear(); }
                finally { _suppressMutualExclusion = false; }
            }
        }

        RefreshMutualExclusion();
    }

    private void OnApiKeyChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressMutualExclusion)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Text))
        {
            if (!string.IsNullOrEmpty(UsernameBox.Text) || !string.IsNullOrEmpty(PasswordBox.Text))
            {
                _suppressMutualExclusion = true;
                try
                {
                    UsernameBox.Clear();
                    PasswordBox.Clear();
                }
                finally { _suppressMutualExclusion = false; }
            }
        }

        RefreshMutualExclusion();
    }

    /// <summary>
    /// Greys out whichever field set is currently inactive so the user can see at a
    /// glance which credential mode is in play. The opposite field set is left enabled
    /// when both are empty so the user can pick either.
    /// </summary>
    private void RefreshMutualExclusion()
    {
        bool upHasText = !string.IsNullOrWhiteSpace(UsernameBox.Text) || !string.IsNullOrWhiteSpace(PasswordBox.Text);
        bool apiHasText = !string.IsNullOrWhiteSpace(ApiKeyBox.Text);

        UsernameBox.IsEnabled = !apiHasText;
        PasswordBox.IsEnabled = !apiHasText;
        ApiKeyBox.IsEnabled = !upHasText;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text?.Trim() ?? string.Empty;
        string password = PasswordBox.Text ?? string.Empty;
        string apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;

        string? hoster = HosterCombo.Visibility == Visibility.Visible
            ? HosterCombo.SelectedItem as string
            : _original.FileHosterName;
        bool supportsApiKey = hoster is not null && HostersWithApiKeyMode.Contains(hoster);

        // For hosters with the dual-mode option, exactly one credential mode must be
        // filled. For all others (U/P only), require both username and password.
        if (supportsApiKey)
        {
            bool hasUp = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
            bool hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

            if (hasUp == hasApiKey)
            {
                // Either both filled or both empty — both are invalid.
                MessageBox.Show(
                    this,
                    Localizer.Instance["EditAccount_Validation_RequireUpOrApiKey"],
                    Localizer.Instance["Common_Error"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                (hasApiKey ? ApiKeyBox : UsernameBox).Focus();
                return;
            }
        }
        else
        {
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
        }

        Result = new FileHosterLoginDto
        {
            Id = _original.Id,
            FileHosterName = hoster ?? _original.FileHosterName,
            Username = username,
            Password = password,
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
            AccountType = _original.AccountType, // Preserved; auto-detected on check/refresh
            Disabled = EnabledCheck.IsChecked != true,
        };

        DialogResult = true;
    }
}
