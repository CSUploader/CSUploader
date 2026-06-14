// <copyright file="StorageAvailableDisplayConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;

namespace CSUploader.Converters;

/// <summary>
/// Renders the Account Manager's "Available" storage cell. Bound to the whole
/// <see cref="FileHosterLoginDto"/> row (not a single property) so it can tell three states
/// apart:
/// <list type="bullet">
///   <item><b>Known cap</b> — <see cref="FileHosterLoginDto.StorageAvailableBytes"/> has a
///   value: render the remaining space in binary IEC units (e.g. "9.36 GiB").</item>
///   <item><b>Unlimited</b> — usage is known but no quota
///   (<see cref="FileHosterLoginDto.StorageUsedBytes"/> set, quota null): the hoster
///   reported storage AND advertised no cap (Ex-Load's <c>storage_left:"inf"</c>). Render
///   the localized "Unlimited" rather than a blank cell.</item>
///   <item><b>Unknown</b> — no storage info at all (both null): blank.</item>
/// </list>
/// </summary>
public class StorageAvailableDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileHosterLoginDto dto)
        {
            return string.Empty;
        }

        if (dto.StorageAvailableBytes is long bytes)
        {
            return ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
        }

        // Usage known but no quota → unlimited (the hoster reported storage and no cap).
        if (dto.StorageUsedBytes is not null)
        {
            return Localizer.Instance["Settings_Accounts_Storage_Unlimited"];
        }

        // No storage info at all.
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
