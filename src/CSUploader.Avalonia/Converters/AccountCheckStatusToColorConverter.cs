// <copyright file="AccountCheckStatusToColorConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CSUploader.Dal;

namespace CSUploader.Converters;

/// <summary>
/// Maps an <see cref="AccountCheckStatus"/> to a foreground brush for the Account Manager
/// grid. Resolves the brush from theme resources at conversion time so the cell colour
/// tracks light/dark swaps instead of staying pinned to a hard-coded RGB that's only
/// legible on one theme.
/// </summary>
/// <remarks>
/// Replaces the earlier <c>StatusToColorConverter</c> which sniffed
/// <see cref="FileHosterLoginDto.StatusMessage"/> for keywords like "Failed" / "Premium"
/// — that quietly painted any unrecognised string green (the catch-all default).
/// Binding to the enum makes the intent explicit and decouples colour from message
/// wording or translation.
/// </remarks>
public class AccountCheckStatusToColorConverter : IValueConverter
{
    // Fallbacks for when no Application is running (designer / unit tests). The greys
    // are neutral enough to be readable on either light or dark surfaces.
    private static readonly IBrush FallbackSuccess = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly IBrush FallbackWarning = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly IBrush FallbackUnchecked = new SolidColorBrush(Color.FromRgb(0xA8, 0xAA, 0xC0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Non-enum input (null at design time, or a wrong binding) → neutral grey rather
        // than throwing. The colour converter is a visual hint, not a correctness check.
        if (value is not AccountCheckStatus status)
        {
            return Resolve("TextDisabledBrush", FallbackUnchecked);
        }

        return status switch
        {
            AccountCheckStatus.Valid => Resolve("SuccessBrush", FallbackSuccess),
            AccountCheckStatus.Failed => Resolve("ErrorBrush", FallbackError),
            AccountCheckStatus.Checking => Resolve("WarningBrush", FallbackWarning),
            // NotChecked and Unsupported both read as "no opinion" — grey rather than
            // implying success or failure.
            _ => Resolve("TextDisabledBrush", FallbackUnchecked),
        };
    }

    private static IBrush Resolve(string resourceKey, IBrush fallback)
    {
        Application? app = Application.Current;

        // Variant-aware lookup: the brush keys live in ThemeBrushes.axaml's ThemeDictionaries, so the
        // ACTIVE variant must be passed — an unscoped lookup misses variant-scoped keys.
        return app is not null && app.TryFindResource(resourceKey, app.ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
