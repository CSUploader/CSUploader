// <copyright file="GalleryWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using CSUploader.Services;

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
    private readonly IThemeApplier _themeApplier;
    private bool _dark;

    // Parameterless ctor kept only for the Avalonia XAML tooling / runtime loader (AVLN3001 — the
    // app itself always uses the IThemeApplier overload via compiled XAML). The no-op applier keeps
    // the toggle buttons from NRE-ing if a tool ever instantiates the window directly.
    public GalleryWindow()
        : this(NoopThemeApplier.Instance)
    {
    }

    public GalleryWindow(IThemeApplier themeApplier)
    {
        _themeApplier = themeApplier;
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
        DialogPlaceholderButton.Click += OnDialogPlaceholder;
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        _themeApplier.ApplyTheme(_dark);
    }

    private void OnApplyGridFont(object? sender, RoutedEventArgs e)
        => _themeApplier.ApplyGridFont("Verdana", 14);

    // Task 1 modal-addressing proof: a trivial inline modal over the gallery. Later dialog tasks
    // replace this with real Dialog<Name>Button launchers into the production IDialogService/window
    // paths. Fire-and-forget ShowDialog — the click handler only needs to open the modal.
    private void OnDialogPlaceholder(object? sender, RoutedEventArgs e)
        => _ = new Window
        {
            Title = "Placeholder modal",
            Width = 300,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = "modal test",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        }.ShowDialog(this);

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
}

/// <summary>Sample DataGrid row (public props for reflection-bound columns).</summary>
public sealed record GalleryRow(string Name, string Size, string State);
