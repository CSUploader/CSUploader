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
/// Theme-token / ThemeVariant-dictionary parity gate plus the real <see cref="AvaloniaThemeApplier"/>.
/// The drift gate parses the WPF <c>Theme.Light.xaml</c> key set (SystemColors overrides filtered out)
/// and asserts it is set-equal to BOTH Avalonia variant dictionaries — so a WPF-side brush addition that
/// is not mirrored into ThemeBrushes.axaml (or a stale Avalonia key) now fails a test, not a view. Reuses
/// the same <see cref="RepoXaml"/> parse as the image drift gate. Runs under
/// <see cref="AvaloniaFactAttribute"/> for the real App's merged resource surface. Tests that mutate
/// process state (RequestedThemeVariant, app.Resources) restore it in a finally.
/// </summary>
public class ThemeTests
{
    private static readonly string RepoRoot = RepoXaml.FindRepoRoot();
    private static readonly string WpfThemePath = Path.Combine(RepoRoot, "src", "Resources", "Theme.Light.xaml");
    private static readonly string WpfThemeDarkPath = Path.Combine(RepoRoot, "src", "Resources", "Theme.Dark.xaml");
    private static readonly string AvaloniaThemePath =
        Path.Combine(RepoRoot, "src", "CSUploader.Avalonia", "Resources", "ThemeBrushes.axaml");
    private static readonly string WpfTokensPath = Path.Combine(RepoRoot, "src", "Resources", "Tokens.xaml");
    private static readonly string AvaloniaTokensPath =
        Path.Combine(RepoRoot, "src", "CSUploader.Avalonia", "Resources", "Tokens.axaml");

    [Fact]
    public void WpfThemeKeys_ParseToLiteralBrushKeys_DroppingSystemColorsOverrides()
    {
        // Guards the drift gate's own input: the WPF theme parses to a real, non-empty literal-key set,
        // and the ONLY keys filtered out are the SystemColors.*BrushKey overrides (WPF-only mechanics).
        HashSet<string> all = RepoXaml.ParseXamlKeys(WpfThemePath);
        HashSet<string> literal = WpfBrushKeys();

        Assert.NotEmpty(literal);
        Assert.Contains("SurfaceBrush", literal);
        Assert.All(literal, k => Assert.DoesNotContain("SystemColors", k, StringComparison.Ordinal));
        Assert.All(all.Except(literal), k => Assert.Contains("SystemColors", k, StringComparison.Ordinal));
    }

    [Fact]
    public void WpfLightAndDarkThemes_HaveIdenticalLiteralKeySets()
    {
        // The drift gate below trusts Theme.Light.xaml as the single WPF source of truth (its comment
        // asserts "Theme.Dark.xaml's set is byte-identical"). Pin that assumption: a brush added to only
        // one WPF variant would otherwise pass the Light-vs-Avalonia gate while the app renders a missing
        // key in the other variant. Symmetric-diff reporting names the one-sided key(s).
        HashSet<string> lightKeys = WpfBrushKeys();
        HashSet<string> darkKeys =
            RepoXaml.ParseXamlKeys(WpfThemeDarkPath).Where(RepoXaml.IsLiteralKey).ToHashSet(StringComparer.Ordinal);

        List<string> lightOnly = lightKeys.Except(darkKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> darkOnly = darkKeys.Except(lightKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(lightOnly.Count == 0, $"brush keys in Theme.Light.xaml but not Theme.Dark.xaml: {string.Join(", ", lightOnly)}");
        Assert.True(darkOnly.Count == 0, $"brush keys in Theme.Dark.xaml but not Theme.Light.xaml: {string.Join(", ", darkOnly)}");
    }

    [AvaloniaFact]
    public void EveryWpfBrushKey_MatchesBothVariantDictionaries_AndResolvesToABrush()
    {
        // Set-equality drift gate: the WPF brush keys must be exactly the key set of EACH Avalonia variant
        // dictionary. A WPF-side brush not mirrored into a variant (or a stale Avalonia key with no WPF
        // source) fails here, naming the offenders — not a downstream view.
        HashSet<string> wpfKeys = WpfBrushKeys();
        (HashSet<string> lightKeys, HashSet<string> darkKeys) = AvaloniaVariantKeys();
        AssertSetEqual("Light", wpfKeys, lightKeys);
        AssertSetEqual("Dark", wpfKeys, darkKeys);

        // …and each ported key actually LOADS as an IBrush under both explicit variant lookups (proves the
        // file text became live resources, not just that the two files agree).
        foreach (string key in wpfKeys)
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

    [Fact]
    public void WpfValueTokens_MatchAvaloniaTokens_KeyAndValue()
    {
        // Third drift gate (image keys, theme brushes, now value tokens): the WPF Tokens.xaml value block
        // and the Avalonia Tokens.axaml must agree on every key AND its value. A WPF-side spacing/sizing
        // change not mirrored into the port (or a stale Avalonia token) fails here, not a downstream view.
        Dictionary<string, string> wpf = RepoXaml.ParseValueTokens(File.ReadAllText(WpfTokensPath));
        Dictionary<string, string> ava = RepoXaml.ParseValueTokens(File.ReadAllText(AvaloniaTokensPath));

        // The plan's Task 5 ports exactly 22 value tokens; pinning the count guards against the parse
        // silently matching a partial subset (which would let a value drift slip). If both files gain a
        // token, bump this number with them.
        Assert.Equal(22, wpf.Count);

        List<string> missing = wpf.Keys.Except(ava.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> stale = ava.Keys.Except(wpf.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, $"WPF value tokens not in Tokens.axaml: {string.Join(", ", missing)}");
        Assert.True(stale.Count == 0, $"Tokens.axaml value tokens with no WPF source (stale): {string.Join(", ", stale)}");

        List<string> mismatched = wpf
            .Where(kv => !ava.TryGetValue(kv.Key, out string? v) || v != kv.Value)
            .Select(kv => $"{kv.Key} (WPF {kv.Value} vs Avalonia {(ava.TryGetValue(kv.Key, out string? v) ? v : "<absent>")})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(mismatched.Count == 0, $"value drift between Tokens.xaml and Tokens.axaml: {string.Join(", ", mismatched)}");
    }

    [AvaloniaFact]
    public void Tokens_ResolveToTheirPortedValues()
    {
        Assert.True(Application.Current!.TryFindResource("SpacingMd", out object? spacing));
        Assert.Equal(new Thickness(8), Assert.IsType<Thickness>(spacing));

        Assert.True(Application.Current!.TryFindResource("ControlHeightSm", out object? height));
        Assert.Equal(24.0, Assert.IsType<double>(height));
    }

    // The portable WPF brush keys: every x:Key in Theme.Light.xaml except the SystemColors.*BrushKey
    // overrides (non-literal markup-extension keys). Theme.Dark.xaml's set is byte-identical.
    private static HashSet<string> WpfBrushKeys()
        => RepoXaml.ParseXamlKeys(WpfThemePath).Where(RepoXaml.IsLiteralKey).ToHashSet(StringComparer.Ordinal);

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

    private static void AssertSetEqual(string variant, HashSet<string> wpf, HashSet<string> ava)
    {
        List<string> missing = wpf.Except(ava).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> stale = ava.Except(wpf).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"WPF brush keys not in the Avalonia {variant} variant dictionary: {string.Join(", ", missing)}");
        Assert.True(
            stale.Count == 0,
            $"Avalonia {variant} variant keys with no WPF source (stale): {string.Join(", ", stale)}");
    }
}
