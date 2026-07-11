// <copyright file="HosterIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace CSUploader.Converters;

/// <summary>
/// Maps a hoster display name to its icon resource. Returns null when no asset exists for
/// the hoster (the cell falls back to text-only).
/// </summary>
public class HosterIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Normalise: drop spaces AND hyphens, lowercase, then look up "FileHoster<Name>Image".
        // Hyphens are stripped so "Ex-Load" resolves to the FileHosterExloadImage resource —
        // the asset names are alphanumeric-only.
        string normalized = name[1..]
            .ToLowerInvariant()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        string key = "FileHoster" + char.ToUpperInvariant(name[0]) + normalized + "Image";
        return Application.Current is { } app && app.TryFindResource(key, out object? resource) ? resource as Bitmap : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
