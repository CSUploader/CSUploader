// <copyright file="StorageAvailableDisplayMultiConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Lib;
using CSUploader.Lib.Localization;

namespace CSUploader.Converters;

/// <summary>
/// Renders the Account Manager's "Available" storage cell. Binds the two NOTIFYING source
/// properties (<c>StorageUsedBytes</c>, <c>StorageQuotaBytes</c>) via a MultiBinding rather than
/// the whole row via an empty-path binding — the latter only re-evaluates when the item instance
/// is replaced, so it rendered stale data once refresh started updating the row in place (the DTO
/// now raises a NAMED PropertyChanged, which an empty-path binding ignores). Three states:
/// <list type="bullet">
///   <item><b>Known cap</b> — both values known: render remaining space (quota − used, floored
///   at 0) in binary IEC units (e.g. "9.36 GiB").</item>
///   <item><b>Unlimited</b> — usage known but no quota: the localized "Unlimited".</item>
///   <item><b>Unknown</b> — no storage info at all (both null): blank.</item>
/// </list>
/// </summary>
public class StorageAvailableDisplayMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        long? used = values is { Length: > 0 } && values[0] is long u ? u : null;
        long? quota = values is { Length: > 1 } && values[1] is long q ? q : null;

        if (used is long uu && quota is long qq)
        {
            // Mirror FileHosterLoginDto.StorageAvailableBytes: clamp at 0 for the over-quota case.
            long available = Math.Max(0L, qq - uu);
            return ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString();
        }

        // Usage known but no quota → unlimited (the hoster reported storage and no cap).
        if (used is not null)
        {
            return Localizer.Instance["Settings_Accounts_Storage_Unlimited"];
        }

        // No storage info at all.
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
