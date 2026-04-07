// <copyright file="DialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using Ookii.Dialogs.Wpf;

namespace CSUploader.Services;

public class DialogService : IDialogService
{
    public void ShowError(string message, string title = "Error")
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool ShowConfirmation(string message, string title = "Confirm")
    {
        MessageBoxResult result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
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
}
