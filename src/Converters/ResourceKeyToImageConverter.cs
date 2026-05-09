// <copyright file="ResourceKeyToImageConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CSUploader.Converters;

/// <summary>
/// Resolves a resource key string (e.g. "StatusSuccessImage") to the actual resource
/// instance (a <c>BitmapImage</c>) declared in <c>ImageResources.xaml</c>. Falls back
/// to <see cref="DependencyProperty.UnsetValue"/> when the key is missing or empty.
/// </summary>
public sealed class ResourceKeyToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return DependencyProperty.UnsetValue;
        }

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
