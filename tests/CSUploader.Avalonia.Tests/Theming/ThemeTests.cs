// <copyright file="ThemeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls; // ResourceNodeExtensions.TryFindResource (2-arg + ThemeVariant overload)
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using CSUploader.Lib;
using CSUploader.Services;
using Moq;

namespace CSUploader.Tests.Avalonia.Theming;

/// <summary>
/// Theme-token / ThemeVariant-dictionary parity gate plus the real <see cref="AvaloniaThemeApplier"/>.
/// The brush keys live in ThemeBrushes.axaml's ThemeDictionaries; the drift gate here asserts all 64
/// resolve in BOTH variants (a WPF-side brush added to only one variant now fails a test, not a view).
/// Runs under <see cref="AvaloniaFactAttribute"/> for the real App's merged resource surface.
/// Tests that mutate process state (RequestedThemeVariant, app.Resources) restore it in a finally.
/// </summary>
public class ThemeTests
{
    // The 64 brush keys ported per variant — pinned by copying the x:Key names from
    // src/Resources/Theme.Light.xaml (SystemColors overrides excluded). Theme.Dark.xaml's key set is
    // byte-identical; this list is the contract both variant dictionaries must satisfy.
    private static readonly string[] BrushKeys =
    [
        "SurfaceBrush", "SurfaceAltBrush", "SurfaceMutedBrush", "BorderBrush",
        "BorderSubtleBrush", "TextPrimaryBrush", "TextSecondaryBrush", "TextDisabledBrush",
        "AccentBrush", "AccentHoverBrush", "AccentForegroundBrush", "ButtonHoverBrush",
        "ButtonPressedBrush", "ScrollBarTrackBrush", "ScrollBarThumbBrush", "ScrollBarThumbHoverBrush",
        "ScrollBarThumbPressedBrush", "DataGridHeaderBrush", "DataGridAltRowBrush", "DataGridGridLineBrush",
        "SelectionBrush", "SelectionForegroundBrush", "ContentBackgroundBrush", "ContentAltBackgroundBrush",
        "RowDetailBackgroundBrush", "ProgressBarTrackBrush", "ProgressBarFillBrush", "ProgressBarAltFillBrush",
        "ProgressBarAltTrackBrush", "OverviewTitleGradientBrush", "ButtonBarGradientBrush", "JD2ButtonBgBrush",
        "JD2ButtonBorderBrush", "JD2ButtonHoverBgBrush", "JD2ButtonHoverBorderBrush", "JD2ButtonPressedBgBrush",
        "JD2ButtonPressedBorderBrush", "ToolbarButtonHoverBrush", "ToolbarButtonHoverBorderBrush", "ToolbarButtonPressedBrush",
        "ToolbarButtonPressedBorderBrush", "SidebarBackgroundBrush", "SidebarHoverBrush", "SidebarSelectedBrush",
        "SidebarSeparatorBrush", "DialogButtonBgBrush", "DialogButtonBorderBrush", "DialogButtonHoverBgBrush",
        "DialogButtonHoverBorderBrush", "DialogButtonPressedBgBrush", "SaveButtonBgBrush", "SaveButtonBorderBrush",
        "SaveButtonHoverBgBrush", "SaveButtonPressedBgBrush", "SuccessBrush", "ErrorBrush",
        "WarningBrush", "OverviewCloseBrush", "OverviewArrowBrush", "InputFieldBorderBrush",
        "LogBackgroundBrush", "LogAltRowBrush", "CodeBackgroundBrush", "InfoAccentBrush",
    ];

    [AvaloniaFact]
    public void BrushKeys_ListIsThe64PortedKeys()
    {
        // Guards the pinned list itself: a fat-finger while copying would otherwise weaken the drift gate.
        Assert.Equal(64, BrushKeys.Length);
        Assert.Equal(64, BrushKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [AvaloniaFact]
    public void EveryBrushKey_ResolvesInBothVariants()
    {
        // Drift gate between the two variant dictionaries: each key must resolve to an IBrush under
        // both an explicit Light and Dark lookup, regardless of the currently-active variant.
        foreach (string key in BrushKeys)
        {
            Assert.True(
                Application.Current!.TryFindResource(key, ThemeVariant.Light, out object? light),
                $"brush key missing in the Light variant dictionary: {key}");
            Assert.IsAssignableFrom<IBrush>(light);

            Assert.True(
                Application.Current!.TryFindResource(key, ThemeVariant.Dark, out object? dark),
                $"brush key missing in the Dark variant dictionary: {key}");
            Assert.IsAssignableFrom<IBrush>(dark);
        }
    }

    [AvaloniaFact]
    public void SurfaceBrush_DiffersBetweenVariants()
    {
        // Pins the variant plumbing (not just key presence): the same key resolves to different colors.
        Assert.True(Application.Current!.TryFindResource("SurfaceBrush", ThemeVariant.Light, out object? light));
        Assert.True(Application.Current!.TryFindResource("SurfaceBrush", ThemeVariant.Dark, out object? dark));

        ISolidColorBrush lightBrush = Assert.IsAssignableFrom<ISolidColorBrush>(light);
        ISolidColorBrush darkBrush = Assert.IsAssignableFrom<ISolidColorBrush>(dark);
        Assert.Equal(Color.Parse("#FFFFFF"), lightBrush.Color);
        Assert.Equal(Color.Parse("#1E1F26"), darkBrush.Color);
    }

    [AvaloniaFact]
    public void ApplyTheme_FlipsRequestedThemeVariant()
    {
        ThemeVariant? original = Application.Current!.RequestedThemeVariant;
        try
        {
            IThemeApplier applier = new AvaloniaThemeApplier(Mock.Of<IAppLogger>());

            applier.ApplyTheme(true);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

            applier.ApplyTheme(false);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void ApplyGridFont_OverwritesTokens()
    {
        try
        {
            new AvaloniaThemeApplier(Mock.Of<IAppLogger>()).ApplyGridFont("Verdana", 14);

            Assert.True(Application.Current!.TryFindResource("GridFontSize", out object? size));
            Assert.Equal(14.0, Assert.IsType<double>(size));

            Assert.True(Application.Current!.TryFindResource("GridFontFamily", out object? family));
            Assert.Contains("Verdana", Assert.IsType<FontFamily>(family).Name, StringComparison.Ordinal);
        }
        finally
        {
            // Un-shadow the merged Tokens.axaml defaults (Tahoma / 12) that ApplyGridFont wrote over.
            Application.Current!.Resources.Remove("GridFontSize");
            Application.Current!.Resources.Remove("GridFontFamily");
        }
    }

    [AvaloniaFact]
    public void Tokens_ResolveToTheirPortedValues()
    {
        Assert.True(Application.Current!.TryFindResource("SpacingMd", out object? spacing));
        Assert.Equal(new Thickness(8), Assert.IsType<Thickness>(spacing));

        Assert.True(Application.Current!.TryFindResource("ControlHeightSm", out object? height));
        Assert.Equal(24.0, Assert.IsType<double>(height));
    }
}
