// <copyright file="ThemeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
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
/// Theme-token / ThemeVariant-dictionary gate plus the real <see cref="AvaloniaThemeApplier"/>. Post-cutover
/// the Avalonia <c>ThemeBrushes.axaml</c> / <c>Tokens.axaml</c> are the sole source of truth: the gates assert
/// the two variant dictionaries carry an identical key set and each key loads as a live IBrush in both
/// variants, and that every value token resolves — so a one-sided or unmerged key fails a test, not a view.
/// Reuses the <see cref="RepoXaml"/> parse. Runs under <see cref="AvaloniaFactAttribute"/> for the real App's
/// merged resource surface. Tests that mutate process state (RequestedThemeVariant, app.Resources) restore it
/// in a finally.
/// </summary>
public class ThemeTests
{
    private static readonly string RepoRoot = RepoXaml.FindRepoRoot();
    private static readonly string AvaloniaThemePath =
        Path.Combine(RepoRoot, "src", "CSUploader.Avalonia", "Resources", "ThemeBrushes.axaml");
    private static readonly string AvaloniaTokensPath =
        Path.Combine(RepoRoot, "src", "CSUploader.Avalonia", "Resources", "Tokens.axaml");

    [Fact]
    public void LightAndDarkVariantDictionaries_HaveIdenticalKeySets()
    {
        // Post-cutover the Avalonia ThemeBrushes.axaml is the sole source of truth. A brush added to only
        // one variant would render a missing key in the other; assert the two variant dictionaries carry an
        // identical literal key set. Symmetric-diff reporting names the one-sided key(s).
        (HashSet<string> lightKeys, HashSet<string> darkKeys) = AvaloniaVariantKeys();
        Assert.NotEmpty(lightKeys);

        List<string> lightOnly = lightKeys.Except(darkKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> darkOnly = darkKeys.Except(lightKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(lightOnly.Count == 0, $"brush keys in the Light variant but not Dark: {string.Join(", ", lightOnly)}");
        Assert.True(darkOnly.Count == 0, $"brush keys in the Dark variant but not Light: {string.Join(", ", darkOnly)}");
    }

    [AvaloniaFact]
    public void EveryBrushKey_ResolvesToABrush_InBothVariants()
    {
        // Drive off the Avalonia ThemeBrushes.axaml key set (sole source of truth post-cutover): every brush
        // key must LOAD as an IBrush under BOTH explicit variant lookups — proving the file text became live
        // resources, not just that the two dictionaries agree on paper. A key resolvable under one variant
        // but not the other fails here, naming the offender; LightAndDarkVariantDictionaries_HaveIdenticalKeySets
        // guards the key-set symmetry itself.
        (HashSet<string> lightKeys, _) = AvaloniaVariantKeys();
        Assert.NotEmpty(lightKeys);

        foreach (string key in lightKeys)
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
            CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(original == ThemeVariant.Dark);
        }
    }

    [AvaloniaFact]
    public void ApplyTheme_AlsoSetsImmersiveDarkCache()
    {
        ThemeVariant? original = Application.Current!.RequestedThemeVariant;
        bool originalDark = CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.IsDark;
        try
        {
            new AvaloniaThemeApplier(Mock.Of<IAppLogger>()).ApplyTheme(true);
            Assert.True(CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.IsDark);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(originalDark);
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
    public void EveryValueToken_ResolvesAtRuntime()
    {
        // Post-cutover the Avalonia Tokens.axaml is the sole source of truth. Parse its value-token block
        // and assert every declared token is merged as a live resource — a token declared in the file but
        // not merged would silently render controls at their fallback size/spacing. NotEmpty guards a silent
        // parse miss; the runtime value of two representative tokens is pinned by Tokens_ResolveToTheirPortedValues.
        Dictionary<string, string> tokens = RepoXaml.ParseValueTokens(File.ReadAllText(AvaloniaTokensPath));
        Assert.NotEmpty(tokens);

        foreach (string key in tokens.Keys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out _), $"value token not merged as a live resource: {key}");
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

    // ThemeBrushes.axaml declares both variant dictionaries in one file; split on the Dark variant marker
    // so each variant's literal key set is isolated (the {x:Static ThemeVariant.*} markers are non-literal).
    private static (HashSet<string> Light, HashSet<string> Dark) AvaloniaVariantKeys()
    {
        string xaml = File.ReadAllText(AvaloniaThemePath);
        // Assumes "ThemeVariant.Dark" occurs exactly once (Light dict first, Dark dict second); a second
        // occurrence would split mid-Dark-section and under-count its keys — safe while the file has one Dark dict.
        int darkIdx = xaml.IndexOf("ThemeVariant.Dark", StringComparison.Ordinal);
        Assert.True(darkIdx > 0, "ThemeBrushes.axaml is missing the Dark variant dictionary marker");

        static HashSet<string> Literal(string section)
            => RepoXaml.ParseXamlKeysFromText(section).Where(RepoXaml.IsLiteralKey).ToHashSet(StringComparer.Ordinal);

        return (Literal(xaml[..darkIdx]), Literal(xaml[darkIdx..]));
    }
}
