// <copyright file="IDialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;

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
    /// Opens a single-select "open file" dialog. <paramref name="filter"/> follows full Win32
    /// filter syntax; <paramref name="defaultExt"/> is appended when the user types a bare name.
    /// Returns the chosen full path, or <c>null</c> on cancel.
    /// </summary>
    public Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null);

    /// <summary>
    /// Opens a "save file" dialog seeded with <paramref name="suggestedFileName"/>.
    /// <paramref name="filter"/> follows full Win32 filter syntax; <paramref name="defaultExt"/> is
    /// appended when the typed name has no extension. Returns the chosen full path, or <c>null</c>
    /// on cancel.
    /// </summary>
    public Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null);

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

    /// <summary>
    /// Shows the request/response viewer (the Logs tab's HTTP details window) for a single
    /// captured transaction — used by the Connection Manager's per-row proxy-test "Details".
    /// Modal; completes when the window closes.
    /// </summary>
    public Task ShowHttpDetailsAsync(HttpTransaction transaction);

    /// <summary>
    /// Shows the Connection Manager's dual-purpose text dialog. In editable mode
    /// (<paramref name="readOnly"/> false) it gathers typed proxy lines and returns them, or
    /// null if cancelled; in read-only mode it displays <paramref name="initialText"/> for
    /// copy-out and returns null (the caller ignores the result).
    /// </summary>
    public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly);

    /// <summary>
    /// Prompts for a per-selection upload speed limit (KB/s), seeded with
    /// <paramref name="currentLimit"/>. Returns null when cancelled (leave limits untouched);
    /// otherwise a <see cref="SpeedLimitSelection"/> whose <see cref="SpeedLimitSelection.LimitKBps"/>
    /// is the chosen limit, or itself null when the user cleared it (revert to the global/inherited value).
    /// </summary>
    public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit);

    /// <summary>
    /// Opens the account editor for <paramref name="account"/> (a fresh DTO for add, an existing
    /// one for edit), offering <paramref name="hosters"/> in the add flow's picker and wiring the
    /// WebView "Sign in" button to <paramref name="interactiveLogin"/>. When
    /// <paramref name="title"/> is null the window keeps its default title (the edit flow).
    /// Returns the populated DTO when the user clicks Save, or null if cancelled.
    /// </summary>
    public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null);
}

/// <summary>
/// Outcome of <see cref="IDialogService.ShowSpeedLimitDialogAsync"/>. A null return from that
/// method means "cancelled — leave limits untouched"; a non-null value carries the chosen limit,
/// where <see cref="LimitKBps"/> is itself null when the user cleared the limit (revert to the
/// global/inherited value). The two-level nullability preserves the WPF dialog's distinction
/// between Cancel (DialogResult false) and Clear (DialogResult true, Result null), which a bare
/// <c>int?</c> return could not express.
/// </summary>
public readonly record struct SpeedLimitSelection(int? LimitKBps);
