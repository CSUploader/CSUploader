// <copyright file="FileTypeIconConverterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Svg.Skia;
using CSUploader.Converters;

namespace CSUploader.Tests.Avalonia.Converters;

/// <summary>
/// Covers the SVG file-type icon pipeline: the converter maps a file name to a vscode-icons SVG,
/// parses it through Svg.Skia (positive <see cref="SvgImage.Size"/> proves the avares asset resolved
/// AND the SVG parsed under SkiaSharp), and caches by icon name so repeated grid rows reuse one image.
/// <see cref="AvaloniaFactAttribute"/> because <c>SvgSource.Load</c> resolves avares resources through
/// the real App's asset loader.
/// </summary>
public class FileTypeIconConverterTests
{
    private readonly FileTypeIconConverter _converter = new();

    [AvaloniaFact]
    public void KnownExtension_ReturnsSvgImage()
    {
        object? result = _converter.Convert("movie.mkv", typeof(object), null, CultureInfo.InvariantCulture);

        SvgImage image = Assert.IsType<SvgImage>(result);
        Assert.True(image.Size.Width > 0, $"expected positive width, got {image.Size.Width}");
        Assert.True(image.Size.Height > 0, $"expected positive height, got {image.Size.Height}");
    }

    [AvaloniaFact]
    public void UnknownExtension_FallsBackToDefaultIcon()
    {
        object? first = _converter.Convert("weird.xyz", typeof(object), null, CultureInfo.InvariantCulture);
        object? second = _converter.Convert("also.abc", typeof(object), null, CultureInfo.InvariantCulture);

        // Both resolve to the single cached default_file icon.
        Assert.IsType<SvgImage>(first);
        Assert.Same(first, second);
    }

    [AvaloniaFact]
    public void PackageRowText_NoExtension_FallsBackToDefaultIcon()
    {
        // Package rows carry a display name with no extension (e.g. "ReScene Files").
        object? result = _converter.Convert("ReScene Files", typeof(object), null, CultureInfo.InvariantCulture);

        Assert.IsType<SvgImage>(result);
    }

    [AvaloniaFact]
    public void RepeatCalls_ServeTheCachedInstance()
    {
        object? first = _converter.Convert("a.mkv", typeof(object), null, CultureInfo.InvariantCulture);
        object? second = _converter.Convert("b.mkv", typeof(object), null, CultureInfo.InvariantCulture);

        // Same icon name (file_type_video) → the exact same parsed SvgImage, not a re-parse per row.
        Assert.Same(first, second);
    }
}
