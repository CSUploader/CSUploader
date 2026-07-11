// <copyright file="RepoXaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Shared helpers for the WPF↔Avalonia parity drift gates (ImageResourceTests, ThemeTests): locate the
/// repo root OutDir-independently and scrape the <c>x:Key</c> set out of a XAML resource dictionary.
/// Extracted from ImageResourceTests so the theme gate can reuse the exact same parse (a WPF-side key
/// addition then fails a test on both the image and theme sides, not a downstream view).
/// </summary>
internal static class RepoXaml
{
    /// <summary>
    /// Every <c>x:Key</c> in a XAML resource dictionary. The keys sit one-per-line in the WPF
    /// ImageResources.xaml / Theme.*.xaml and the Avalonia ImageGeometries.axaml / ThemeBrushes.axaml,
    /// so a flat scan is exact. The captured value is the raw attribute text, so markup-extension keys
    /// (e.g. <c>{x:Static SystemColors.GrayTextBrushKey}</c>) come back verbatim and the caller filters
    /// them — see <see cref="IsLiteralKey"/>.
    /// </summary>
    internal static HashSet<string> ParseXamlKeys(string path) => ParseXamlKeysFromText(File.ReadAllText(path));

    /// <summary>
    /// <see cref="ParseXamlKeys"/> over an in-memory slice of XAML — used to isolate one variant
    /// dictionary out of a single file that declares several (ThemeBrushes.axaml's Light + Dark).
    /// </summary>
    internal static HashSet<string> ParseXamlKeysFromText(string xaml)
        => Regex.Matches(xaml, "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True for a plain string key; false for a markup-extension key such as
    /// <c>{x:Static SystemColors.MenuBrushKey}</c> or the <c>{x:Static ThemeVariant.Light}</c> variant
    /// markers — the WPF-only mechanics the ports deliberately drop.
    /// </summary>
    internal static bool IsLiteralKey(string key) => !key.StartsWith('{');

    // The keyed value-token element types shared by the WPF Tokens.xaml value block and the Avalonia
    // Tokens.axaml. WPF writes `sys:Double`, Avalonia `x:Double`; both have the local name "Double", so
    // the prefix is dropped and only the local type is compared. Style / ControlTemplate keys use other
    // element names and are excluded by this set (belt) and by the <Style … slice (braces) below.
    private static readonly Regex ValueTokenPattern =
        new("<(?:\\w+:)?(Double|FontFamily|Thickness|CornerRadius|GridLength)\\s+x:Key=\"([^\"]+)\"\\s*>([^<]*)</", RegexOptions.Compiled);

    /// <summary>
    /// Value tokens (spacing/typography/sizing/corners + the grid font) as a <c>key → "Type=value"</c>
    /// map, for the Tokens.xaml ↔ Tokens.axaml drift gate. Everything from the first <c>&lt;Style</c>
    /// onward is sliced off first: the WPF Tokens.xaml re-templates (deliberately NOT ported) declare
    /// their own keyed resources, and the value block always precedes them. Normalizing to
    /// <c>"Type=value"</c> makes a WPF <c>sys:Double 13</c> compare equal to an Avalonia <c>x:Double 13</c>
    /// while still catching a type OR value drift.
    /// </summary>
    internal static Dictionary<string, string> ParseValueTokens(string xaml)
    {
        int styleIdx = xaml.IndexOf("<Style", StringComparison.Ordinal);
        string block = styleIdx >= 0 ? xaml[..styleIdx] : xaml;
        return ValueTokenPattern.Matches(block)
            .ToDictionary(m => m.Groups[2].Value, m => $"{m.Groups[1].Value}={m.Groups[3].Value.Trim()}", StringComparer.Ordinal);
    }

    /// <summary>
    /// Repo root via <see cref="CallerFilePathAttribute"/>, NOT <c>AppContext.BaseDirectory</c>: the repo
    /// builds to a temp OutDir (<c>D:\temp2\…</c>) to dodge bin locks, so the binary's directory is outside
    /// the tree. Same pattern + rationale as <c>I18nRegenGateTests.FindRepoRoot</c>. The injected path is the
    /// caller's source file, which lives in the repo regardless of which test calls this.
    /// </summary>
    internal static string FindRepoRoot([CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = Directory.GetParent(thisFilePath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSUploader.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + thisFilePath);
    }
}
