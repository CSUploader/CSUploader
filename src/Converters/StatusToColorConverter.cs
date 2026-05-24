// <copyright file="StatusToColorConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CSUploader.Converters;

/// <summary>
/// Converts a status message string to a color brush for display. Resolves the brush
/// from theme resources at conversion time so the color tracks light/dark swaps
/// instead of staying pinned to a hard-coded RGB that's only legible on one theme.
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    // Fallbacks for when no Application is running (designer / unit tests). The
    // greys are neutral enough to be readable on either light or dark surfaces.
    private static readonly Brush FallbackSuccess = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush FallbackError = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush FallbackWarning = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush FallbackUnchecked = new SolidColorBrush(Color.FromRgb(0xA8, 0xAA, 0xC0));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string status || string.IsNullOrEmpty(status))
        {
            return Resolve("TextDisabledBrush", FallbackUnchecked);
        }

        if (string.Equals(status, "Not checked", StringComparison.Ordinal))
        {
            return Resolve("TextDisabledBrush", FallbackUnchecked);
        }

        if (status.Contains("Premium", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Free account", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve("SuccessBrush", FallbackSuccess);
        }

        if (status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve("ErrorBrush", FallbackError);
        }

        if (status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve("ErrorBrush", FallbackError);
        }

        if (status.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Checking", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve("WarningBrush", FallbackWarning);
        }

        // Unknown text — fall back to neutral grey rather than green. The old default of
        // SuccessBrush would silently paint any failure the converter didn't recognise
        // (e.g. raw network exception messages like "The SSL connection could not be
        // established...") as if the account were valid. Callers that want success
        // colouring must produce text that hits one of the positive rules above.
        return Resolve("TextDisabledBrush", FallbackUnchecked);
    }

    private static Brush Resolve(string resourceKey, Brush fallback)
        => Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
