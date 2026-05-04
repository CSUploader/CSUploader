// <copyright file="FileStateIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Maps a <see cref="FileState"/> to a status icon resource so the Uploads grid can show
/// a JDownloader-style icon next to the status text.
/// </summary>
public class FileStateIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileState state)
        {
            return null;
        }

        string key = state switch
        {
            FileState.Idle => "StatusQueuedImage",
            FileState.HashQueued or FileState.UploadQueued => "StatusQueuedImage",
            FileState.Hashing => "StatusHashingImage",
            FileState.Uploading => "StatusUploadingImage",
            FileState.Completed => "StatusSuccessImage",
            FileState.Failed => "StatusFailedImage",
            FileState.Paused => "StatusWarningImage",
            FileState.Cancelled => "StatusCancelledImage",
            _ => "StatusQueuedImage",
        };

        return Application.Current?.TryFindResource(key) as BitmapImage;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
