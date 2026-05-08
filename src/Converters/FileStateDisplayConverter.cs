// <copyright file="FileStateDisplayConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Converters;

public class FileStateDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Bindings that hit this converter are re-evaluated when the row's State changes.
        // A live language switch won't re-render rows whose state isn't moving (Completed,
        // Failed, …) until they change again — acceptable trade-off vs. wiring every row VM
        // to Localizer.PropertyChanged.
        string? key = value switch
        {
            FileState.Idle => "Uploads_State_Idle",
            FileState.HashQueued => "Uploads_State_HashQueued",
            FileState.Hashing => "Uploads_State_Hashing",
            FileState.UploadQueued => "Uploads_State_UploadQueued",
            FileState.Uploading => "Uploads_State_Uploading",
            FileState.Completed => "Uploads_State_Completed",
            FileState.Failed => "Uploads_State_Failed",
            FileState.Paused => "Uploads_State_Paused",
            FileState.Cancelled => "Uploads_State_Cancelled",
            _ => null,
        };
        return key is null ? string.Empty : Localizer.Instance[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
