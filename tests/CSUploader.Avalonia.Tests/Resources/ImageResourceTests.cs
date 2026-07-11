// <copyright file="ImageResourceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using CSUploader.Resources;

namespace CSUploader.Tests.Avalonia.Resources;

/// <summary>
/// WPF key-parity gate for the ported image resources. The keys are load-bearing — the runtime
/// converters (HosterIconConverter computes "FileHoster&lt;Name&gt;Image"; the status/action
/// converters look keys up by name) resolve against these, so parity is enforced by test, not by
/// eyeball. Runs under <see cref="AvaloniaFactAttribute"/> because it needs the real
/// <c>App</c> instance's merged resource surface (bitmaps merged in <c>App.Initialize</c>, geometries
/// via <c>App.axaml</c>).
/// </summary>
public class ImageResourceTests
{
    [AvaloniaFact]
    public void PortedKeys_MatchWpfImageResources_AndBitmapsLoad()
    {
        // Real drift gate (replaces the old self-referential count pin): parse the WPF
        // ImageResources.xaml x:Key set (bitmaps AND geometries) and assert it is set-equal to the
        // Avalonia port — BitmapImageResources.Entries keys plus the merged ImageGeometries.axaml
        // keys. A WPF-side key addition that is not mirrored here (the Buzzheavier master-merge
        // scenario) now FAILS this test instead of silently rendering a blank icon. Source files are
        // located via CallerFilePath (OutDir-independent — the repo builds to a temp OutDir; same
        // pattern as I18nRegenGateTests.FindRepoRoot).
        string root = RepoXaml.FindRepoRoot();
        HashSet<string> wpfKeys = RepoXaml.ParseXamlKeys(Path.Combine(root, "src", "Resources", "ImageResources.xaml"));
        HashSet<string> geometryKeys = RepoXaml.ParseXamlKeys(
            Path.Combine(root, "src", "CSUploader.Avalonia", "Resources", "ImageGeometries.axaml"));
        HashSet<string> portedKeys = BitmapImageResources.Entries
            .Select(e => e.Key)
            .Concat(geometryKeys)
            .ToHashSet(StringComparer.Ordinal);

        // Symmetric-difference reporting so a drift names the offending key(s).
        List<string> missing = wpfKeys.Except(portedKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> stale = portedKeys.Except(wpfKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"WPF ImageResources.xaml keys not ported (add to BitmapImageResources.Entries or ImageGeometries.axaml): {string.Join(", ", missing)}");
        Assert.True(
            stale.Count == 0,
            $"Ported keys with no WPF source (stale port): {string.Join(", ", stale)}");

        // Runtime check: every bitmap entry resolves to an actually-loaded Bitmap.
        foreach ((string key, _) in BitmapImageResources.Entries)
        {
            Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
            Assert.IsType<Bitmap>(value); // a wrong path would have thrown in MergeInto already
        }
    }

    [AvaloniaFact]
    public void LoadBearingComputedKeys_Exist()
    {
        // The exact keys HosterIconConverter computes for the awkward names (dots, hyphens):
        foreach (string key in (string[])["FileHosterStorage.toImage", "FileHosterTransfer.itImage",
            "FileHosterFilehoster.ioImage", "FileHosterExloadImage", "StatusSuccessImage", "StatusFailedImage"])
        {
            Assert.True(Application.Current!.TryFindResource(key, out _), $"missing resource: {key}");
        }
    }

    [AvaloniaFact]
    public void EveryGeometryKey_Resolves()
    {
        foreach (string key in (string[])["SettingsLanguageGeometry", "SettingsDeveloperGeometry",
            "SettingsGridAppearanceGeometry", "SettingsWindowBehaviourGeometry", "SettingsConfirmationGeometry",
            "SettingsNotificationsGeometry", "SettingsDatabaseGeometry", "ForceStartGeometry"])
        {
            Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
            Assert.IsAssignableFrom<global::Avalonia.Media.Geometry>(value);
        }
    }
}
