// <copyright file="ProgressWidthConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;

namespace CSUploader.Converters;

/// <summary>
/// Converts a progress percentage (0-100) and a container width to a proportional width value.
/// Used for the layered Border progress bar pattern in tree views.
/// </summary>
public class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double progress
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            double clampedProgress = Math.Clamp(progress, 0.0, 100.0);
            return containerWidth * clampedProgress / 100.0;
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
