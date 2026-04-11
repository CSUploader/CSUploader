// <copyright file="SingleOrArrayJsonConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib.Extensions;

public class SingleOrArrayJsonConverter<T> : JsonConverter<T[]>
{
    public override T[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<T> items = [];
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return [.. items];
                }

                T? item = JsonSerializer.Deserialize<T>(ref reader, options);
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return [.. items];
        }

        T? singleItem = JsonSerializer.Deserialize<T>(ref reader, options);
        return singleItem != null ? [singleItem] : [];
    }

    public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
    {
        if (value.Length == 1)
        {
            JsonSerializer.Serialize(writer, value[0], options);
        }
        else
        {
            JsonSerializer.Serialize<T[]>(writer, value, options);
        }
    }
}
