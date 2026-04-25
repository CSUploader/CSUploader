// <copyright file="SpeedLimitConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;

namespace CSUploader.Converters;

public class SpeedLimitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int kbps && kbps > 0)
        {
            return kbps >= 1024
                ? $"{kbps / 1024.0:0.##} MB/s"
                : $"{kbps} KB/s";
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
