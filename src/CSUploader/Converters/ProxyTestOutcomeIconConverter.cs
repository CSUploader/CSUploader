// <copyright file="ProxyTestOutcomeIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using CSUploader.ViewModels;

namespace CSUploader.Converters;

/// <summary>
/// Resolves a <see cref="ProxyTestOutcome"/> to its status-icon resource (green check / red X)
/// so the Connection Manager grid can show a result icon next to each proxy.
/// <see cref="ProxyTestOutcome.Untested"/> resolves to null so the cell stays blank.
/// </summary>
public class ProxyTestOutcomeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProxyTestOutcome outcome)
        {
            return null;
        }

        string? resourceKey = outcome switch
        {
            ProxyTestOutcome.Ok => "StatusOkImage",
            ProxyTestOutcome.Failed => "StatusFailedImage",
            _ => null,
        };

        return resourceKey is not null && Application.Current is { } app && app.TryFindResource(resourceKey, out object? resource)
            ? resource
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
