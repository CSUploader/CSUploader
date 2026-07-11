// <copyright file="ResourceKeyToImageConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace CSUploader.Converters;

/// <summary>
/// Resolves a resource key string (e.g. "StatusSuccessImage") to the actual resource
/// instance (a <c>Bitmap</c>) declared in the merged image resources. Falls back
/// to <see cref="AvaloniaProperty.UnsetValue"/> when the key is missing or empty.
/// </summary>
public sealed class ResourceKeyToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return AvaloniaProperty.UnsetValue;
        }

        return Application.Current is { } app && app.TryFindResource(key, out object? resource) && resource is not null
            ? resource
            : AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
