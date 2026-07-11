using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
/// than yanking the tray-hidden main window up). The four StorageProvider pickers are real from Phase 4
/// Task 4 (they reveal the main window first via <see cref="GetOwnerOrRevealAsync"/>, since a native
/// picker needs a visible parent); the remaining ported dialog windows arrive through the later Phase 4
/// tasks, and the three account/proxy editor members stay <see cref="NotImplementedException"/> until
/// Phase 5 builds their windows.
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

    // ── StorageProvider pickers (Phase 4 Task 4) ─────────────────────────────────────────────────
    // The four Browse members replace the WPF head's Ookii.Dialogs + Microsoft.Win32 dialogs
    // (src/Services/DialogService.cs:49-126). The owner (hence the StorageProvider) is resolved through
    // GetOwnerOrRevealAsync so a tray-hidden main window is revealed first — a native picker demands a
    // visible parent. Selections map back to on-disk paths via TryGetLocalPath(): the WPF contract is a
    // string path, and Windows local-disk picks always carry one; a picked item from a non-filesystem
    // provider (TryGetLocalPath == null) is treated as no selection (Reality-check #16). The option
    // construction is factored into the internal Build* builders below because the pickers themselves are
    // native and cannot run headlessly (Reality-check #11) — the builders are the pinnable, tested half.

    public async Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null)
    {
        IStorageProvider storage = (await GetOwnerOrRevealAsync()).StorageProvider;

        // TryGetFolderFromPathAsync returns null on a missing/invalid path (never throws — Reality-check
        // #10), which just means "no suggested start location".
        IStorageFolder? start = string.IsNullOrEmpty(initialDirectory)
            ? null
            : await storage.TryGetFolderFromPathAsync(initialDirectory);

        IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(
            BuildFolderOptions(title ?? Localizer.Instance["Common_SelectFolder"], start));

        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null)
    {
        IStorageProvider storage = (await GetOwnerOrRevealAsync()).StorageProvider;

        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(
            BuildOpenOptions(title ?? Localizer.Instance["Common_SelectFiles"], filter, multiple: true));

        if (picked.Count == 0)
        {
            return null;
        }

        // Drop any non-filesystem picks (null local path); an all-non-local selection collapses to the
        // WPF cancel contract (null).
        string[] paths = [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
        return paths.Length > 0 ? paths : null;
    }

    // defaultExt is a documented no-op on Avalonia: open pickers have no "append this extension to a bare
    // typed name" concept (that belongs to the save picker only). The parameter is kept for interface
    // parity with the WPF head and dropped silently here.
    public async Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null)
    {
        IStorageProvider storage = (await GetOwnerOrRevealAsync()).StorageProvider;

        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(
            BuildOpenOptions(title ?? Localizer.Instance["Common_SelectFiles"], filter, multiple: false));

        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null)
    {
        IStorageProvider storage = (await GetOwnerOrRevealAsync()).StorageProvider;

        IStorageFile? picked = await storage.SaveFilePickerAsync(
            BuildSaveOptions(suggestedFileName, filter, defaultExt));

        return picked?.TryGetLocalPath();
    }

    // Option builders: pure, framework-only construction so they can be pinned by unit tests while the
    // native pickers above cannot run headlessly. WPF's Win32 filter string is mapped 1:1 to
    // FilePickerFileType via the Core FileDialogFilterParser (already unit-tested); an empty/absent filter
    // yields null (Avalonia's "no filter"). Titles are passed in already-resolved (the members apply the
    // Localizer default) so these stay Localizer-free.

    internal static FolderPickerOpenOptions BuildFolderOptions(string title, IStorageFolder? suggestedStartLocation) =>
        new()
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
        };

    internal static FilePickerOpenOptions BuildOpenOptions(string title, string? filter, bool multiple) =>
        new()
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = MapFilter(filter),
        };

    // No Title: mirrors the WPF SaveFileDialog, which sets none. DefaultExtension gets the bare extension
    // — every WPF caller passes ".txt"/".json" and Avalonia wants "txt"/"json" (the prep-item wrinkle).
    // ShowOverwritePrompt is left unset (bool? default null = "use the platform default", which is
    // prompt-on-overwrite on Windows — WPF SaveFileDialog parity; the plan's "default true" was off).
    internal static FilePickerSaveOptions BuildSaveOptions(string? suggestedName, string? filter, string? defaultExt) =>
        new()
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = string.IsNullOrEmpty(defaultExt) ? null : defaultExt.TrimStart('.'),
            FileTypeChoices = MapFilter(filter),
        };

    private static IReadOnlyList<FilePickerFileType>? MapFilter(string? filter)
    {
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries = FileDialogFilterParser.Parse(filter);
        return entries.Count == 0
            ? null
            : [.. entries.Select(e => new FilePickerFileType(e.Name) { Patterns = e.Patterns })];
    }

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
