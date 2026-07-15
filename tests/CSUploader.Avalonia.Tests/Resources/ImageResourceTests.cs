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
using SkiaSharp;

namespace CSUploader.Tests.Avalonia.Resources;

/// <summary>
/// Load-bearing gate for the ported image resources. The keys are load-bearing — the runtime converters
/// (HosterIconConverter computes "FileHoster&lt;Name&gt;Image"; the status/action converters look keys up
/// by name) resolve against these, so a missing/blank icon is caught by test, not by eyeball. Runs under
/// <see cref="AvaloniaFactAttribute"/> because it needs the real <c>App</c> instance's merged resource
/// surface (bitmaps merged in <c>App.Initialize</c>, geometries via <c>App.axaml</c>).
/// </summary>
public class ImageResourceTests
{
    [AvaloniaFact]
    public void EveryBitmapEntry_ResolvesToLoadedBitmap()
    {
        // Post-cutover the Avalonia head is the sole source of truth (the WPF ImageResources.xaml drift
        // reference is gone with the head). Assert every BitmapImageResources entry resolves in the App's
        // merged resource surface to a real loaded Bitmap — the coverage that catches a hoster icon added
        // to the map but not actually merged/shipped (the Buzzheavier master-merge scenario), which would
        // otherwise render blank. Geometry keys are covered by EveryGeometryKey_Resolves.
        Assert.NotEmpty(BitmapImageResources.Entries);
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

    [Fact]
    public void EveryBitmapAsset_DecodesWithSkia()
    {
        // The headless session stubs bitmap loading (TestAppBuilder keeps UseSkia OFF), so the
        // [AvaloniaFact] resource checks above prove a key resolves but NOT that the PNG/ICO bytes are a
        // decodable image — a corrupt or truncated asset would still hand back a stub. Decode each asset
        // straight off disk with SkiaSharp (no Avalonia platform needed) to pin real decodability. Paths
        // mirror the head's AvaloniaResource glob root: src/Properties/Images/<entry.Path>.
        string imagesRoot = Path.Combine(RepoXaml.FindRepoRoot(), "src", "Properties", "Images");
        Assert.NotEmpty(BitmapImageResources.Entries);

        foreach ((string key, string path) in BitmapImageResources.Entries)
        {
            string file = Path.Combine(imagesRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"asset file missing on disk for key {key}: {file}");

            using SKBitmap? bitmap = SKBitmap.Decode(file);
            Assert.True(bitmap is not null, $"SkiaSharp could not decode the asset for key {key}: {file}");
            Assert.True(bitmap!.Width > 0 && bitmap.Height > 0, $"decoded asset has no pixels for key {key}: {file}");
        }
    }

    [AvaloniaFact]
    public void EveryGeometryKey_Resolves()
    {
        // Drive off the file itself, not a hand-maintained key list: a geometry added to
        // ImageGeometries.axaml is then covered automatically (and one removed can't leave a stale
        // assertion passing against a key that no longer exists). All keys in the file are literal.
        HashSet<string> geometryKeys = RepoXaml.ParseXamlKeys(
            Path.Combine(RepoXaml.FindRepoRoot(), "src", "CSUploader.Avalonia", "Resources", "ImageGeometries.axaml"))
            .Where(RepoXaml.IsLiteralKey).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(geometryKeys);

        foreach (string key in geometryKeys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
            Assert.IsAssignableFrom<global::Avalonia.Media.Geometry>(value);
        }
    }
}
