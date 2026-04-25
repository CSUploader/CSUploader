// <copyright file="TimeSpanFormatConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;

namespace CSUploader.Converters;

public class TimeSpanFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return timeSpan.ToString(@"h\h\:mm\m\:ss\s", CultureInfo.InvariantCulture);
            }

            if (timeSpan.TotalMinutes >= 1)
            {
                return timeSpan.ToString(@"mm\m\:ss\s", CultureInfo.InvariantCulture);
            }

            return timeSpan.ToString(@"ss\s", CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
