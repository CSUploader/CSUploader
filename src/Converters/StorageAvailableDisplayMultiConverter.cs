// <copyright file="StorageAvailableDisplayMultiConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Converters;

/// <summary>
/// Renders the Account Manager's "Available" storage cell. Binds the two NOTIFYING source
/// properties (<c>StorageUsedBytes</c>, <c>StorageQuotaBytes</c>) plus <c>FileHosterName</c> via a
/// MultiBinding rather than the whole row via an empty-path binding — the latter only re-evaluates
/// when the item instance is replaced, so it rendered stale data once refresh started updating the
/// row in place (the DTO now raises a NAMED PropertyChanged, which an empty-path binding ignores).
/// States:
/// <list type="bullet">
///   <item><b>Known cap</b> — both values known: remaining space (quota − used, floored at 0) in
///   binary IEC units (e.g. "9.36 GiB").</item>
///   <item><b>Unlimited</b> — usage known but no quota (Upstore/GigaPeta report used + no cap), OR a
///   known-unlimited hoster that exposes no usage at all (catbox — see
///   <see cref="FileHosterClient.HasUnlimitedStorage"/>): the localized "Unlimited".</item>
///   <item><b>Unknown</b> — no storage info and not a known-unlimited hoster (usage we couldn't
///   retrieve): "-".</item>
/// </list>
/// </summary>
public class StorageAvailableDisplayMultiConverter : IMultiValueConverter
{
    private const string Unknown = "-";

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        long? used = values is { Length: > 0 } && values[0] is long u ? u : null;
        long? quota = values is { Length: > 1 } && values[1] is long q ? q : null;
        string? hosterName = values is { Length: > 2 } ? values[2] as string : null;

        if (used is long uu && quota is long qq)
        {
            // Mirror FileHosterLoginDto.StorageAvailableBytes: clamp at 0 for the over-quota case.
            long available = Math.Max(0L, qq - uu);
            return ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString();
        }

        // Unlimited: the hoster reported usage and no cap, OR it's a known-unlimited hoster that
        // exposes no usage number at all (catbox).
        if (used is not null || FileHosterClient.HasUnlimitedStorage(hosterName))
        {
            return Localizer.Instance["Settings_Accounts_Storage_Unlimited"];
        }

        // No storage info and not known-unlimited → we couldn't retrieve it.
        return Unknown;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
