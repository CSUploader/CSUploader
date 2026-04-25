// <copyright file="FileStateDisplayConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Upload;

namespace CSUploader.Converters;

public class FileStateDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FileState.Idle => "Idle",
        FileState.HashQueued => "Hash Queued",
        FileState.Hashing => "Hashing",
        FileState.UploadQueued => "Upload Queued",
        FileState.Uploading => "Uploading",
        FileState.Completed => "Completed",
        FileState.Failed => "Failed",
        FileState.Paused => "Paused",
        FileState.Cancelled => "Cancelled",
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
