// <copyright file="DialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Dal;
using CSUploader.Upload;
using CSUploader.Views;
using Ookii.Dialogs.Wpf;

namespace CSUploader.Services;

public class DialogService : IDialogService
{
    private readonly AppSettings _settings;
    private readonly SettingRepository _settingRepository;

    public DialogService(AppSettings settings, SettingRepository settingRepository)
    {
        _settings = settings;
        _settingRepository = settingRepository;
    }

    public void ShowError(string message, string title = "Error") => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool ShowConfirmation(string message, string title = "Confirm")
    {
        MessageBoxResult result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public bool ShowOptOutConfirmation(string confirmationKey, string message, string title = "Confirm")
    {
        if (_settings.SuppressedConfirmations.Contains(confirmationKey))
        {
            return true;
        }

        ConfirmationDialog dialog = new(message, title);
        bool? result = dialog.ShowDialog();
        if (result != true || !dialog.Confirmed)
        {
            return false;
        }

        if (dialog.DontAskAgain)
        {
            _settings.SuppressedConfirmations.Add(confirmationKey);

            // Fire-and-forget the DB write; if it fails the user will just be asked again
            // on next action, which is an acceptable fallback.
            _ = PersistSuppressedAsync();
        }

        return true;
    }

    public string? BrowseFolder(string? initialDirectory = null, string title = "Select Folder")
    {
        VistaFolderBrowserDialog dialog = new()
        {
            Description = title,
            UseDescriptionForTitle = true,
        };

        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.SelectedPath = initialDirectory;
        }

        bool? dialogResult = dialog.ShowDialog();
        return dialogResult == true ? dialog.SelectedPath : null;
    }

    private async Task PersistSuppressedAsync()
    {
        try
        {
            string value = string.Join(",", _settings.SuppressedConfirmations);
            SettingDto? existing = await _settingRepository.FindByKeyAsync(SettingKey.SuppressedConfirmations);
            if (existing is not null)
            {
                existing.Value = value;
                await _settingRepository.UpdateAsync(existing);
            }
            else
            {
                await _settingRepository.InsertAsync(new SettingDto { Key = SettingKey.SuppressedConfirmations, Value = value });
            }
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
