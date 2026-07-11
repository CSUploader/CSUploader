// <copyright file="UrlDecodeConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data.Converters;

namespace CSUploader.Converters;

/// <summary>
/// Renders a percent-encoded URL in its human-readable, url-decoded form. Used by the
/// Logs &gt; HTTP tab's URL column. Null/empty/non-string input renders as the empty string;
/// a malformed percent sequence falls back to the original string rather than throwing.
/// </summary>
public class UrlDecodeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (Exception)
        {
            return s;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
