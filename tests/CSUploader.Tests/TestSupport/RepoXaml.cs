// <copyright file="RepoXaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Shared helpers for the Avalonia resource/theme gates (ImageResourceTests, ThemeTests): locate the repo
/// root OutDir-independently and scrape the <c>x:Key</c> set out of a XAML resource dictionary. Post-cutover
/// the Avalonia dictionaries are the sole source of truth, so a key present in one variant but not its
/// sibling (or a token declared but not merged) fails a test here, not a downstream view.
/// </summary>
internal static class RepoXaml
{
    /// <summary>
    /// Every <c>x:Key</c> in a XAML resource dictionary. The keys sit one-per-line in the Avalonia
    /// ImageGeometries.axaml / ThemeBrushes.axaml, so a flat scan is exact. The captured value is the raw
    /// attribute text, so markup-extension keys (e.g. the <c>{x:Static ThemeVariant.Dark}</c> variant
    /// marker) come back verbatim and the caller filters them — see <see cref="IsLiteralKey"/>.
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

    // The keyed value-token element types in the Avalonia Tokens.axaml value block. The element-name
    // prefix (e.g. x:Double) is optional in the pattern and dropped, so only the local type is compared.
    // Style / ControlTemplate keys use other element names and are excluded by this set (belt) and by the
    // <Style … slice below.
    private static readonly Regex ValueTokenPattern =
        new("<(?:\\w+:)?(Double|FontFamily|Thickness|CornerRadius|GridLength)\\s+x:Key=\"([^\"]+)\"\\s*>([^<]*)</", RegexOptions.Compiled);

    /// <summary>
    /// Value tokens (spacing/typography/sizing/corners + the grid font) of the Avalonia Tokens.axaml as a
    /// <c>key → "Type=value"</c> map. Everything from the first <c>&lt;Style</c> onward is sliced off first
    /// so any keyed Style/ControlTemplate resources (which follow the value block) are not scraped as value
    /// tokens. Normalizing to <c>"Type=value"</c> captures both the element type and its value.
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
