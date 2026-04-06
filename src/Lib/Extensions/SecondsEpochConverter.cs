// <copyright file="SecondsEpochConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib.Extensions;

public class SecondsEpochConverter : JsonConverter<DateTime>
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            double seconds = reader.GetDouble();
            return Epoch.AddSeconds(seconds);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (value != null && double.TryParse(value, out double seconds))
            {
                return Epoch.AddSeconds(seconds);
            }
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue((value - Epoch).TotalSeconds);
    }
}
