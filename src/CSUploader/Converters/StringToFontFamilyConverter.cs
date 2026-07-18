// <copyright file="StringToFontFamilyConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CSUploader.Converters;

/// <summary>
/// Turns a font-family NAME string into a <see cref="FontFamily"/>. Used by the Grid Appearance
/// font picker's per-item preview, whose items are plain strings: binding a string straight to a
/// <see cref="FontFamily"/> target logs "Could not convert 'Tahoma' (String) to FontFamily" because
/// the binding pipeline doesn't run the type converter there. This does the conversion explicitly.
/// </summary>
public class StringToFontFamilyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string name && !string.IsNullOrWhiteSpace(name) ? new FontFamily(name) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as FontFamily)?.Name;
}
