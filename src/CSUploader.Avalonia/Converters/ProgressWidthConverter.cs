// <copyright file="ProgressWidthConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data.Converters;

namespace CSUploader.Converters;

/// <summary>
/// Converts a progress percentage (0-100) and a container width to a proportional width value.
/// Used for the layered Border progress bar pattern in tree views. Avalonia
/// <see cref="IMultiValueConverter"/>: values arrive as an <c>IList&lt;object?&gt;</c>, and unset
/// bindings arrive as <c>AvaloniaProperty.UnsetValue</c> (not null) — the <c>is double</c>
/// pattern-matches guard both, falling through to 0.
/// </summary>
public class ProgressWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2
            && values[0] is double progress
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            double clampedProgress = Math.Clamp(progress, 0.0, 100.0);
            return containerWidth * clampedProgress / 100.0;
        }

        return 0.0;
    }
}
