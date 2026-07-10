// <copyright file="ImageResourceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
    public void EveryBitmapEntry_ResolvesToALoadedBitmap()
    {
        Assert.Equal(69, BitmapImageResources.Entries.Length); // count pinned to ImageResources.xaml:8-88
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
