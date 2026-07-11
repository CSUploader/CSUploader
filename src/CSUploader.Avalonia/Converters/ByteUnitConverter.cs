// <copyright file="ByteUnitConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data.Converters;
using CSUploader.Lib;

namespace CSUploader.Converters;

public class ByteUnitConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
        }

        // No usable value (null / non-long). Callers that want a visible placeholder for "unknown"
        // pass it as ConverterParameter (the Accounts grid's Used column passes "-" for hosters that
        // report no usage, e.g. catbox). TargetNullValue can't do this here — with a converter in the
        // pipeline the null reaches Convert first, so this is where the placeholder must be honoured.
        // Everyone else passes no parameter and keeps the original blank.
        return parameter as string ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
