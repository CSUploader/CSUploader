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
    public Task ShowErrorAsync(string message, string? title = null);

    public Task<bool> ShowConfirmationAsync(string message, string? title = null);

    /// <summary>
    /// Shows a confirmation dialog with a "Don't ask me again" checkbox. If the user has
    /// previously opted out for <paramref name="confirmationKey"/>, returns true silently
    /// without prompting. When the user ticks "Don't ask again" and clicks Yes, the opt-out
    /// is persisted to the settings store.
    /// </summary>
    /// <param name="confirmationKey">Stable key from <see cref="ConfirmationKeys"/>.</param>
    public Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null);

    public Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null);

    /// <summary>
    /// Opens a multi-select file dialog. <paramref name="filter"/> follows full Win32
    /// filter syntax (e.g. <c>"All files|*.*"</c>); <c>null</c> means no filter.
    /// Returns the array of full paths chosen, or <c>null</c> on cancel.
    /// </summary>
    public Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null);

    /// <summary>
    /// Opens the add-account editor preselected to <paramref name="hosterName"/> (locked
    /// when only one hoster is provided). Returns the new account when the user clicks
    /// Save, or null if the dialog was cancelled.
    /// </summary>
    public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, string? title = null);

    /// <summary>
    /// Opens the proxy editor dialog seeded with <paramref name="seed"/>. Pass a fresh
    /// DTO (Id=0) for the add flow; pass an existing DTO for edit. Returns the populated
    /// proxy when the user clicks Save, or null if cancelled.
    /// </summary>
    public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null);
}
