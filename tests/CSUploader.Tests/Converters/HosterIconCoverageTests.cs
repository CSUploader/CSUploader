// <copyright file="HosterIconCoverageTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using CSUploader.Converters;
using CSUploader.Upload;

namespace CSUploader.Tests.Converters;

/// <summary>
/// Every shipping hoster must actually resolve an icon.
/// <para>
/// This exists because two of them silently didn't. <see cref="HosterIconConverter"/> builds its
/// resource key by lower-casing everything after the first letter — so a hoster named "TeraBytez"
/// looks for <c>FileHosterTerabytezImage</c>, and an entry registered as
/// <c>FileHosterTeraBytezImage</c> (the obvious spelling) never matches. The converter returns null
/// on a miss and the cell falls back to text, so nothing fails, nothing logs, and the icon is just
/// quietly absent — which is exactly how it shipped in v1.2.0 for TeraBytez and DataVaults.
/// </para>
/// <para>
/// Asserting through the REAL converter rather than re-implementing its normalisation is the point:
/// a test that duplicated the key-building rule could be wrong in exactly the same way as the code.
/// </para>
/// </summary>
public class HosterIconCoverageTests
{
    [AvaloniaFact]
    public void EveryShippingHoster_ResolvesAnIcon()
    {
        HosterIconConverter converter = new();

        string[] missing = [.. FileHosterClient.FileHosters.Keys
            .Where(name => converter.Convert(name, typeof(Bitmap), null, CultureInfo.InvariantCulture) is null)
            .OrderBy(name => name, StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            "These hosters resolve no icon — check the key casing in BitmapImageResources.Entries "
                + "(the converter lower-cases everything after the first letter):"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
    }

    [AvaloniaTheory]
    // The three that were mis-cased, kept as explicit cases so a regression names itself.
    [InlineData("TeraBytez")]
    [InlineData("DataVaults")]
    [InlineData("Easybytez")]
    // …and a couple whose names are awkward for other reasons: a leading digit, a hyphen, and dots
    // that must NOT be stripped.
    [InlineData("1Fichier")]
    [InlineData("Ex-Load")]
    [InlineData("Storage.to")]
    public void AwkwardlyNamedHosters_StillResolve(string hosterName)
    {
        HosterIconConverter converter = new();
        Assert.NotNull(converter.Convert(hosterName, typeof(Bitmap), null, CultureInfo.InvariantCulture));
    }
}
