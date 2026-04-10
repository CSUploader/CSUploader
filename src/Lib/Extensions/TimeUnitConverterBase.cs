// <copyright file="TimeUnitConverterBase.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib.Extensions;

public abstract class TimeUnitConverterBase : JsonConverter<TimeSpan>
{
    protected abstract TimeSpan FromValue(double value);

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (value != null && double.TryParse(value, CultureInfo.InvariantCulture, out double parsed))
            {
                return FromValue(parsed);
            }
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            double parsed = reader.GetDouble();
            return FromValue(parsed);
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
    }
}
