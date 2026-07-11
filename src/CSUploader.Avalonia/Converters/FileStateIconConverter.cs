// <copyright file="FileStateIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Maps a <see cref="FileState"/> to a status icon resource so the Uploads grid can show
/// a JDownloader-style icon next to the status text.
/// </summary>
public class FileStateIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
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
            FileState.CompletedWithErrors => "StatusWarningImage",
            FileState.Failed => "StatusFailedImage",
            FileState.Paused => "StatusWarningImage",
            FileState.Cancelled => "StatusCancelledImage",
            _ => "StatusQueuedImage",
        };

        return Application.Current is { } app && app.TryFindResource(key, out object? resource) ? resource as Bitmap : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
