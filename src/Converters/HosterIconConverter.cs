// <copyright file="HosterIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CSUploader.Converters;

/// <summary>
/// Maps a hoster display name to its icon resource. Returns null when no asset exists for
/// the hoster (the cell falls back to text-only).
/// </summary>
public class HosterIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Normalise: drop spaces, lowercase, then look up "FileHoster<Name>Image".
        string key = "FileHoster" + char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal) + "Image";
        return Application.Current?.TryFindResource(key) as BitmapImage;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
