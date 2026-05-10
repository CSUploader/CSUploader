// <copyright file="IDialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

namespace CSUploader.Services;

public interface IDialogService
{
    /// <summary>
    /// Default titles (Error / Confirm / Select Folder) come from <c>Localizer</c> when omitted,
    /// so language switches are reflected on the next call.
    /// </summary>
    void ShowError(string message, string? title = null);

    bool ShowConfirmation(string message, string? title = null);

    /// <summary>
    /// Shows a confirmation dialog with a "Don't ask me again" checkbox. If the user has
    /// previously opted out for <paramref name="confirmationKey"/>, returns true silently
    /// without prompting. When the user ticks "Don't ask again" and clicks Yes, the opt-out
    /// is persisted to the settings store.
    /// </summary>
    /// <param name="confirmationKey">Stable key from <see cref="ConfirmationKeys"/>.</param>
    bool ShowOptOutConfirmation(string confirmationKey, string message, string? title = null);

    string? BrowseFolder(string? initialDirectory = null, string? title = null);

    /// <summary>
    /// Opens a multi-select file dialog. <paramref name="filter"/> follows full Win32
    /// filter syntax (e.g. <c>"All files|*.*"</c>); <c>null</c> means no filter.
    /// Returns the array of full paths chosen, or <c>null</c> on cancel.
    /// </summary>
    string[]? BrowseFiles(string? title = null, string? filter = null);

    /// <summary>
    /// Opens the add-account editor preselected to <paramref name="hosterName"/> (locked
    /// when only one hoster is provided). Returns the new account when the user clicks
    /// Save, or null if the dialog was cancelled.
    /// </summary>
    FileHosterLoginDto? ShowAddAccountDialog(string hosterName, string[] availableHosters, string? title = null);
}
