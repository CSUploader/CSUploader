// <copyright file="DaysTimespanConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib.Extensions;

public class DaysTimespanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (value != null && double.TryParse(value, out double days))
            {
                return TimeSpan.FromDays(days);
            }
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            double days = reader.GetDouble();
            return TimeSpan.FromDays(days);
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
