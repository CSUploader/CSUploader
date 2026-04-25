// <copyright file="IDialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

public interface IDialogService
{
    void ShowError(string message, string title = "Error");

    bool ShowConfirmation(string message, string title = "Confirm");

    /// <summary>
    /// Shows a confirmation dialog with a "Don't ask me again" checkbox. If the user has
    /// previously opted out for <paramref name="confirmationKey"/>, returns true silently
    /// without prompting. When the user ticks "Don't ask again" and clicks Yes, the opt-out
    /// is persisted to the settings store.
    /// </summary>
    /// <param name="confirmationKey">Stable key from <see cref="ConfirmationKeys"/>.</param>
    bool ShowOptOutConfirmation(string confirmationKey, string message, string title = "Confirm");

    string? BrowseFolder(string? initialDirectory = null, string title = "Select Folder");
}
