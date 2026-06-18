// <copyright file="ItemStateToVisibilityConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Converts a row item (Package or PackageFile) to Visibility based on its state.
/// ConverterParameter: "Startable", "ForceStartable", or "Stoppable" (default).
/// </summary>
public class ItemStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        FileState state = value switch
        {
            Package pkg => pkg.State,
            PackageFile file => file.State,
            _ => FileState.Idle,
        };

        string mode = parameter as string ?? string.Empty;

        bool visible = mode switch
        {
            // Normal Start: only files that aren't yet in the pipeline.
            "Startable" => state is FileState.Idle or FileState.Cancelled or FileState.Failed or FileState.Paused,

            // Force start: anything not currently running and not finished — including a file
            // already queued and waiting for a slot (HashQueued/UploadQueued), which is the
            // core "jump the concurrency limit" case.
            "ForceStartable" => state is FileState.Idle or FileState.HashQueued or FileState.UploadQueued
                or FileState.Paused or FileState.Failed or FileState.Cancelled,

            // Stoppable (default): files currently in the pipeline.
            _ => state is FileState.Hashing or FileState.Uploading or FileState.HashQueued or FileState.UploadQueued,
        };

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
