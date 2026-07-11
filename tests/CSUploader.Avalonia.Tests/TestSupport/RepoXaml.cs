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
