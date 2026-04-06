// <copyright file="ByteUnitJsonConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib;

public class ByteUnitJsonConverter : JsonConverter<ByteUnit>
{
    public override ByteUnit? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (value != null && ByteUnit.TryParseSize(value, out ByteUnit? byteUnit))
            {
                return byteUnit;
            }
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            double bytes = reader.GetDouble();
            return new ByteUnit(bytes, ByteUnitSymbol.B);
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, ByteUnit value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToFriendlyString());
    }
}
