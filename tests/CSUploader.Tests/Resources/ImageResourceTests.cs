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

    [Fact]
    public void EveryHosterIcon_HasEnoughVisibleInk()
    {
        // A resolvable, decodable icon can still be invisible. Xubster shipped as a 160x28 wordmark
        // scaled to fit 64px wide, which left a SEVEN-pixel-tall strip of near-black text on
        // transparency — present, decodable, resolving through the converter, and unnoticeable in the
        // grid (worst in the dark theme). Every other check here passed on it.
        //
        // 6% is calibrated, not guessed: across the 83 icons shipping today the thinnest legible one
        // covers 9.7%, and the sliver above covered 4.2%. Coverage is the metric that actually failed,
        // so it is the only one asserted — a second rule on shape or contrast would be a guess.
        const double MinimumOpaqueFraction = 0.06;

        string imagesRoot = Path.Combine(RepoXaml.FindRepoRoot(), "src", "Properties", "Images");
        List<string> faint = [];

        foreach ((string key, string path) in BitmapImageResources.Entries)
        {
            if (!path.StartsWith("FileHosters/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string file = Path.Combine(imagesRoot, path.Replace('/', Path.DirectorySeparatorChar));
            using SKBitmap? bitmap = SKBitmap.Decode(file);
            Assert.True(bitmap is not null, $"could not decode {key}: {file}");

            int opaque = 0;
            for (int y = 0; y < bitmap!.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 32)
                    {
                        opaque++;
                    }
                }
            }

            double fraction = (double)opaque / (bitmap.Width * bitmap.Height);
            if (fraction < MinimumOpaqueFraction)
            {
                faint.Add($"{key} covers {fraction:P1} of its canvas ({file})");
            }
        }

        Assert.True(
            faint.Count == 0,
            "These hoster icons are too faint to see in the grid — a wordmark squeezed to fit the width "
                + "leaves a sliver. Prefer the site's square favicon (a 4x nearest-neighbour upscale of a "
                + "16x16 stays crisp):" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", faint));
    }

    [AvaloniaFact]
    public void EveryGeometryKey_Resolves()
    {
        // Drive off the file itself, not a hand-maintained key list: a geometry added to
        // ImageGeometries.axaml is then covered automatically (and one removed can't leave a stale
        // assertion passing against a key that no longer exists). All keys in the file are literal.
        HashSet<string> geometryKeys = RepoXaml.ParseXamlKeys(
            Path.Combine(RepoXaml.FindRepoRoot(), "src", "CSUploader", "Resources", "ImageGeometries.axaml"))
            .Where(RepoXaml.IsLiteralKey).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(geometryKeys);

        foreach (string key in geometryKeys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
            Assert.IsAssignableFrom<global::Avalonia.Media.Geometry>(value);
        }
    }
}
