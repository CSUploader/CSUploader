// <copyright file="PackagePriorityDisplayConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Maps a <see cref="PackagePriority"/> to its localized display label
/// (Highest / High / Normal / Low / Lowest) for the Uploads-tab Priority column.
/// </summary>
public class PackagePriorityDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? key = value switch
        {
            PackagePriority.Highest => "Uploads_Priority_Highest",
            PackagePriority.High => "Uploads_Priority_High",
            PackagePriority.Normal => "Uploads_Priority_Normal",
            PackagePriority.Low => "Uploads_Priority_Low",
            PackagePriority.Lowest => "Uploads_Priority_Lowest",
            _ => null,
        };
        return key is null ? string.Empty : Localizer.Instance[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
