// <copyright file="IDialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

public interface IDialogService
{
    void ShowError(string message, string title = "Error");

    bool ShowConfirmation(string message, string title = "Confirm");

    string? BrowseFolder(string? initialDirectory = null, string title = "Select Folder");
}
