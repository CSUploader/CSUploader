// <copyright file="StartMenuLabelConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data.Converters;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Returns the localized "Start now" label for rows whose package has a future
/// <see cref="Package.ScheduledStartTime"/> (so the right-click action visibly
/// overrides the schedule); falls back to the regular "Start" label otherwise.
/// Bound from the row context menu's Start <c>MenuItem.Header</c>.
/// </summary>
public class StartMenuLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTime? scheduled = value switch
        {
            Package pkg => pkg.ScheduledStartTime,
            PackageFile file => file.ScheduledStartTime,
            _ => null,
        };

        bool future = scheduled is { } at && at > DateTime.Now;
        return Localizer.Instance[future ? "Uploads_Context_StartNow" : "Uploads_Context_Start"];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
