using Avalonia.Controls;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia <see cref="IDialogService"/> on the shared <see cref="DialogServiceBase"/> (which owns the
/// opt-out suppression lookup + "don't ask again" persistence). The notification/confirmation surface
/// is real from Phase 4 Task 3: <see cref="ShowErrorAsync"/>, <see cref="ShowConfirmationAsync"/>, and
/// the base's opt-out flow all route through <see cref="MessageBoxWindow"/> and the shared
/// <see cref="DialogOwnerResolver"/> (with the null-owner policy: a message box shows ownerless rather
/// than yanking the tray-hidden main window up). The StorageProvider pickers and the ported dialog
/// windows arrive through the remaining Phase 4 tasks; the three account/proxy editor members stay
/// <see cref="NotImplementedException"/> until Phase 5 builds their windows.
/// </summary>
public sealed class AvaloniaDialogService(AppSettings settings, SettingRepository settingRepository, ITrayIconService trayIcon)
    : DialogServiceBase(settings, settingRepository), IDialogService
{
    public Task ShowErrorAsync(string message, string? title = null) =>
        MessageBoxWindow.ShowErrorAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title ?? Localizer.Instance["Common_Error"]);

    public Task<bool> ShowConfirmationAsync(string message, string? title = null) =>
        MessageBoxWindow.ShowConfirmationAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title ?? Localizer.Instance["Common_Confirm"]);

    protected override async Task<(bool Confirmed, bool DontAskAgain)> ShowOptOutConfirmationCoreAsync(string message, string title)
    {
        MessageBoxOutcome outcome = await MessageBoxWindow.ShowOptOutAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title);
        return (outcome.Confirmed, outcome.DontAskAgain);
    }

    // The shared owner-or-reveal composition the modal dialogs and pickers consume (Phase 4 Tasks 4/5/7,
    // Phase 5). Unlike a message box, a modal interaction demands a visible parent, so a null resolution
    // reveals the tray-hidden main window first. ShowMainWindow() Show()s + Activate()s synchronously on
    // the UI thread and Show() sets IsVisible synchronously, so the window is immediately resolvable —
    // no dispatcher hop needed (Reality-check #7). Task-returning so a later task can await an Opened hop
    // here without touching the callers if that assumption ever changes.
    private Task<Window> GetOwnerOrRevealAsync()
    {
        Window? owner = DialogOwnerResolver.ResolveFromLifetime();
        if (owner is null)
        {
            trayIcon.ShowMainWindow();
            owner = DialogOwnerResolver.ResolveFromLifetime()
                ?? throw new InvalidOperationException("No window available to own a dialog.");
        }

        return Task.FromResult(owner);
    }

    public Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null) =>
        throw new NotImplementedException("StorageProvider pickers arrive in Phase 4 Task 4 — BrowseFolderAsync tracked there.");

    public Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null) =>
        throw new NotImplementedException("StorageProvider pickers arrive in Phase 4 Task 4 — BrowseFilesAsync tracked there.");

    public Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null) =>
        throw new NotImplementedException("StorageProvider pickers arrive in Phase 4 Task 4 — BrowseOpenFileAsync tracked there.");

    public Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null) =>
        throw new NotImplementedException("StorageProvider pickers arrive in Phase 4 Task 4 — BrowseSaveFileAsync tracked there.");

    public Task ShowHttpDetailsAsync(HttpTransaction transaction) =>
        throw new NotImplementedException("HttpDetailsWindow arrives in Phase 4 Task 7 — ShowHttpDetailsAsync tracked there.");

    public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly) =>
        throw new NotImplementedException("ProxyTextDialog arrives in Phase 4 Task 5 — ShowProxyTextDialogAsync tracked there.");

    public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit) =>
        throw new NotImplementedException("SpeedLimitDialog arrives in Phase 4 Task 5 — ShowSpeedLimitDialogAsync tracked there.");

    public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) =>
        throw new NotImplementedException("EditAccountWindow arrives in Phase 5 — ShowAddAccountDialogAsync tracked there.");

    public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) =>
        throw new NotImplementedException("EditAccountWindow arrives in Phase 5 — ShowEditAccountDialogAsync tracked there.");

    public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null) =>
        throw new NotImplementedException("EditProxyWindow arrives in Phase 5 — ShowEditProxyDialogAsync tracked there.");
}
