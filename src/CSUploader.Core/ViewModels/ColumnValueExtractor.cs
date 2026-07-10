// <copyright file="ColumnValueExtractor.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Reflection;

namespace CSUploader.ViewModels;

/// <summary>
/// Reads a single column's value off a row VM for the per-column "Copy" right-click
/// submenu on the Uploads / Uploaded tabs. The column-key alphabet matches the
/// <c>{Uploads,Uploaded}_Col_*</c> resx suffix so XAML drives the menu without a
/// separate enum.
/// </summary>
internal static class ColumnValueExtractor
{
    /// <summary>
    /// Returns the row's value for <paramref name="columnKey"/>, formatted for clipboard
    /// pasting, or <c>null</c> if the value is absent. The Uploads tab has heterogeneous
    /// rows (Package + PackageFile) but both expose the same property names that the
    /// DataGrid columns bind to, so reflection covers both.
    /// </summary>
    public static string? Extract(object row, string columnKey, bool isUploadsTab)
    {
        string propertyName = MapColumnKeyToProperty(columnKey, isUploadsTab);
        PropertyInfo? prop = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop is null)
        {
            return null;
        }

        object? value = prop.GetValue(row);
        return Format(value);
    }

    private static string MapColumnKeyToProperty(string columnKey, bool isUploadsTab)
    {
        // Most column keys match the property name on the row VM directly. The handful
        // that don't are listed here. Keep these in sync with the bindings in
        // UploadsView.xaml / UploadedView.xaml.
        if (isUploadsTab)
        {
            return columnKey switch
            {
                "Hoster" => "HosterDisplay",
                "Account" => "AccountDisplay",
                "Status" => "State",
                "ETA" => "TimeRemaining",
                "Added" => "AddedDate",
                "Finished" => "FinishedDate",
                "Started" => "StartedDate",
                "ScheduledAt" => "ScheduledStartTime",
                "SpeedLimit" => "EffectiveSpeedLimitKBps",
                "Hash" => "FileHash",
                "URL" => "FileUrl",
                "Order" => "QueueOrder",
                _ => columnKey,
            };
        }

        return columnKey switch
        {
            "Name" => "FileName",
            "Path" => "FileDirectory",
            "Size" => "FileSize",
            "Hoster" => "FileHosterName",
            "Account" => "AccountDisplay",
            "Finished" => "FinishedDateTime",
            "Started" => "StartedDateTime",
            "Hash" => "FileHash",
            "URL" => "FileUrl",
            _ => columnKey,
        };
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        string s => string.IsNullOrEmpty(s) ? null : s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString(),
    };
}
