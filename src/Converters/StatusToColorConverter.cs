// <copyright file="StatusToColorConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CSUploader.Converters;

/// <summary>
/// Converts a status message string to a color brush for display.
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    private static readonly Brush ValidBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x2E));   // Green
    private static readonly Brush FailedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));  // Red
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0x8A, 0x00)); // Amber
    private static readonly Brush UncheckedBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)); // Gray
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));   // Red

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string status || string.IsNullOrEmpty(status))
        {
            return UncheckedBrush;
        }

        if (string.Equals(status, "Not checked", StringComparison.Ordinal))
        {
            return UncheckedBrush;
        }

        if (status.Contains("Premium", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Free account", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            return ValidBrush;
        }

        if (status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorBrush;
        }

        if (status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
        {
            return FailedBrush;
        }

        if (status.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Checking", StringComparison.OrdinalIgnoreCase))
        {
            return WarningBrush;
        }

        return ValidBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
