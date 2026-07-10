using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Placeholder <see cref="IDialogService"/> for Phase 2. Implemented directly (not on
/// <c>DialogServiceBase</c>, which Phase 4 rewrites it onto) with every member throwing — the
/// Avalonia dialog stack (StorageProvider pickers, ported dialog windows) arrives in Phase 4. Its
/// presence lets the shared ViewModels resolve; any code path that actually opens a dialog fails
/// loudly rather than silently doing nothing.
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public Task ShowErrorAsync(string message, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowErrorAsync tracked there.");

    public Task<bool> ShowConfirmationAsync(string message, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowConfirmationAsync tracked there.");

    public Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowOptOutConfirmationAsync tracked there.");

    public Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — BrowseFolderAsync tracked there.");

    public Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — BrowseFilesAsync tracked there.");

    public Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — BrowseOpenFileAsync tracked there.");

    public Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — BrowseSaveFileAsync tracked there.");

    public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowAddAccountDialogAsync tracked there.");

    public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowEditProxyDialogAsync tracked there.");

    public Task ShowHttpDetailsAsync(HttpTransaction transaction) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowHttpDetailsAsync tracked there.");

    public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowProxyTextDialogAsync tracked there.");

    public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowSpeedLimitDialogAsync tracked there.");

    public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) =>
        throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — ShowEditAccountDialogAsync tracked there.");
}
