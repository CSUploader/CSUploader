// <copyright file="SingleLineConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;

namespace CSUploader.Converters;

/// <summary>
/// Display-side flattener: replaces CR/LF runs in a string with single spaces so the
/// value renders on one row in a DataGrid cell. Underlying property keeps its original
/// shape — copy-to-clipboard (via <c>ClipboardContentBinding</c> bound directly to the
/// source) still surfaces newlines intact. Primary user: the Uploads-tab Error column,
/// where pipelines occasionally pass through multi-line server-error payloads (BRupload's
/// HTML 500 page being the canonical worst case).
/// </summary>
public class SingleLineConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s))
        {
            return value;
        }

        return s
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
}
