// <copyright file="GalleryWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.DevTools;

/// <summary>
/// DEBUG-only dev gallery (opened by <c>--gallery</c>): a standing style/token test page that
/// exercises every Phase 3 primitive — token brushes, typography classes, the four button styles,
/// inputs, a behavior-attached DataGrid, all four icon families, and the LocExtension — in one
/// window so the migration contact sheet can judge the theme/density port. Not shipped in Release
/// (the trigger in <c>App.axaml.cs</c> is <c>#if DEBUG</c>).
/// </summary>
/// <remarks>
/// The two named buttons drive the REAL <see cref="IThemeApplier"/> paths so the bridge screenshots
/// capture production behavior: <c>ThemeToggleButton</c> flips the theme variant (the SettingsViewModel
/// path) and <c>GridFontButton</c> writes the grid-font resources (proving live DynamicResource
/// propagation into the DataGrid — the verification Task 5 deferred to this window).
/// </remarks>
public partial class GalleryWindow : Window
{
    // Synthesized long error for the ErrorDetails driver: a human-readable summary plus a raw HTML
    // response snippet, mirroring what an XFileSharing sign-in failure carries into the window (the WPF
    // reference driver feeds an equivalent shape). All fake — no dialog driver reads real account state.
    private const string SynthErrorDetail =
        "Sign-in failed: invalid credentials for the selected hoster.\n\n" +
        "<html><head><title>403 Forbidden</title></head><body>\n" +
        "<h1>Access denied</h1>\n" +
        "<p>The username or password you entered is incorrect, or the account has been suspended " +
        "for exceeding the free-tier upload quota. Please verify your credentials on the hoster's " +
        "website and try again. If the problem persists the login endpoint may be rate-limiting this " +
        "IP address; wait a few minutes before retrying.</p>\n" +
        "<div class=\"error-code\" data-request-id=\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\">" +
        "Reference: XFS-403-CREDENTIALS · edge node fra-07 · 2026-07-11T14:22:09Z</div>\n" +
        "<!-- upstream: nginx/1.24.0; cf-ray 8ab12cd34ef5-FRA; retry-after 300 -->\n" +
        "</body></html>";

    private readonly IThemeApplier _themeApplier;
    private readonly IDialogService _dialogService;
    private bool _dark;

    // Parameterless ctor kept only for the Avalonia XAML tooling / runtime loader (AVLN3001 — the
    // app itself always uses the injecting overload via compiled XAML). The no-op services keep the
    // launcher buttons from NRE-ing if a tool ever instantiates the window directly.
    public GalleryWindow()
        : this(NoopThemeApplier.Instance, NoopDialogService.Instance)
    {
    }

    public GalleryWindow(IThemeApplier themeApplier, IDialogService dialogService)
    {
        _themeApplier = themeApplier;
        _dialogService = dialogService;
        InitializeComponent();

        SampleGrid.ItemsSource = new[]
        {
            new GalleryRow("fake_movie.mkv", "5.0 MB", "Paused"),
            new GalleryRow("fake_notes.txt", "1.0 MB", "Paused"),
            new GalleryRow("fake_archive.zip", "3.0 MB", "Failed"),
            new GalleryRow("fake_song.mp3", "2.0 MB", "Completed"),
            new GalleryRow("fake_photo.jpg", "1.0 MB", "Completed"),
        };

        // Each item is a file-name string bound through FileTypeIconConverter in the XAML template;
        // d.xyz is unknown on purpose (falls back to default_file — still a rendered SVG).
        FileTypeIconsPanel.ItemsSource = new[] { "a.mkv", "b.zip", "c.pdf", "d.xyz" };

        // Seed the toggle from the CURRENT variant so the first click always flips visibly: startup
        // applies the persisted theme (WPF parity) before this window opens, so the app is usually
        // already Dark by the time the gallery shows — a bool that started false would no-op the
        // first click.
        _dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        ThemeToggleButton.Click += OnToggleTheme;
        GridFontButton.Click += OnApplyGridFont;
        DialogErrorButton.Click += OnShowError;
        DialogConfirmButton.Click += OnShowConfirm;
        DialogOptOutButton.Click += OnShowOptOut;
        DialogErrorDetailsButton.Click += OnShowErrorDetails;
        DialogProxyTextEditButton.Click += OnShowProxyTextEdit;
        DialogProxyTextExportButton.Click += OnShowProxyTextExport;
        DialogSpeedLimitButton.Click += OnShowSpeedLimit;
        PickFolderButton.Click += OnPickFolder;
        PickFilesButton.Click += OnPickFiles;
        PickOpenFileButton.Click += OnPickOpenFile;
        PickSaveFileButton.Click += OnPickSaveFile;
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        _themeApplier.ApplyTheme(_dark);
    }

    private void OnApplyGridFont(object? sender, RoutedEventArgs e)
        => _themeApplier.ApplyGridFont("Verdana", 14);

    // The three message-box launchers call the REAL resolved IDialogService (the gallery is the active
    // window, so the resolver owns the box to it — modal over the gallery). Fire-and-forget: the launcher
    // only needs to open the modal; the bridge reads the outcome by driving the buttons, not the return.
    private void OnShowError(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowErrorAsync("Sign-in failed: invalid credentials for the selected hoster.");

    private void OnShowConfirm(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowConfirmationAsync("Delete 3 selected packages?\nThis cannot be undone.");

    // Fresh suppression key per click: a ticked "Yes" persists harmlessly to the scratch DB without
    // silently suppressing the next drive (production keys off ConfirmationKeys, not a random Guid).
    private void OnShowOptOut(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowOptOutConfirmationAsync(
            "gallery-" + Guid.NewGuid().ToString("N"),
            "Remove this account and all of its uploads?\nThis cannot be undone.");

    // ── Text-centric dialogs (Phase 4 Task 5) ──
    // ErrorDetails has no IDialogService member (Phase 5's EditAccountWindow "Details" link opens it), so
    // the gallery constructs the window directly — the same new ErrorDetailsWindow(...).ShowDialog(this)
    // the WPF reference driver uses. ProxyText/SpeedLimit go through the REAL resolved IDialogService (the
    // gallery is the active window, so the resolver owns each modal to it), driving production plumbing.
    private void OnShowErrorDetails(object? sender, RoutedEventArgs e)
        => _ = new ErrorDetailsWindow(SynthErrorDetail).ShowDialog(this);

    private void OnShowProxyTextEdit(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowProxyTextDialogAsync(
            "Import proxies",
            "One proxy per line, host:port[:user:pass].",
            "127.0.0.1:8080\n10.0.0.1:1080:user:pass",
            readOnly: false);

    private void OnShowProxyTextExport(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowProxyTextDialogAsync(
            "Export proxies",
            "One proxy per line, host:port[:user:pass].",
            "127.0.0.1:8080\n10.0.0.1:1080:user:pass",
            readOnly: true);

    private void OnShowSpeedLimit(object? sender, RoutedEventArgs e)
        => _ = _dialogService.ShowSpeedLimitDialogAsync(512);

    // The four picker launchers call the REAL IDialogService picker members (native OS dialogs). They are
    // "manual only": the bridge cannot drive or screenshot a native modal, so the agent never clicks them
    // in a session — they are here for manual smoke and a crash-free open check. Each writes the
    // returned path(s) into PickerResultText. Representative filters/defaultExt exercise the real
    // filter-parse + TrimStart paths (the JSON-export shape UploadedViewModel uses).
    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        string? path = await _dialogService.BrowseFolderAsync();
        PickerResultText.Text = path is null ? "Picker result: folder — (cancelled)" : $"Picker result: folder — {path}";
    }

    private async void OnPickFiles(object? sender, RoutedEventArgs e)
    {
        string[]? paths = await _dialogService.BrowseFilesAsync(filter: "All files (*.*)|*.*");
        PickerResultText.Text = paths is null
            ? "Picker result: files — (cancelled)"
            : $"Picker result: files — {string.Join(", ", paths)}";
    }

    private async void OnPickOpenFile(object? sender, RoutedEventArgs e)
    {
        string? path = await _dialogService.BrowseOpenFileAsync(
            filter: "JSON files (*.json)|*.json|All files (*.*)|*.*", defaultExt: ".json");
        PickerResultText.Text = path is null ? "Picker result: open — (cancelled)" : $"Picker result: open — {path}";
    }

    private async void OnPickSaveFile(object? sender, RoutedEventArgs e)
    {
        string? path = await _dialogService.BrowseSaveFileAsync(
            suggestedFileName: "export.json",
            filter: "JSON files (*.json)|*.json|All files (*.*)|*.*",
            defaultExt: ".json");
        PickerResultText.Text = path is null ? "Picker result: save — (cancelled)" : $"Picker result: save — {path}";
    }

    /// <summary>No-op <see cref="IThemeApplier"/> for the tooling-only parameterless ctor.</summary>
    private sealed class NoopThemeApplier : IThemeApplier
    {
        public static readonly NoopThemeApplier Instance = new();

        public void ApplyGridFont(string family, double size)
        {
        }

        public void ApplyTheme(bool isDark)
        {
        }
    }

    /// <summary>No-op <see cref="IDialogService"/> for the tooling-only parameterless ctor (the preview
    /// host never invokes a launcher, so these never run in practice).</summary>
    private sealed class NoopDialogService : IDialogService
    {
        public static readonly NoopDialogService Instance = new();

        public Task ShowErrorAsync(string message, string? title = null) => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(string message, string? title = null) => Task.FromResult(false);

        public Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null) => Task.FromResult(false);

        public Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null) => Task.FromResult<string?>(null);

        public Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null) => Task.FromResult<string[]?>(null);

        public Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null) => Task.FromResult<string?>(null);

        public Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null) => Task.FromResult<string?>(null);

        public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) => Task.FromResult<FileHosterLoginDto?>(null);

        public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null) => Task.FromResult<FileHosterLoginDto?>(null);

        public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null) => Task.FromResult<ProxySettingDto?>(null);

        public Task ShowHttpDetailsAsync(HttpTransaction transaction) => Task.CompletedTask;

        public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly) => Task.FromResult<string?>(null);

        public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit) => Task.FromResult<SpeedLimitSelection?>(null);
    }
}

/// <summary>Sample DataGrid row (public props for reflection-bound columns).</summary>
public sealed record GalleryRow(string Name, string Size, string State);
