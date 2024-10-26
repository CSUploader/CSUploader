// <copyright file="SingleOrArrayJsonConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSUploader.Extensions
{
    public class SingleOrArrayJsonConverter<T> : JsonConverter
    {
        public override bool CanWrite => true;

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<T>);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            if (token.Type == JTokenType.Array)
            {
                if (token.HasValues)
                {
                    T[]? items = token.ToObject<T[]>();
                    if (items != null)
                    {
                        return items;
                    }
                }

                return Array.Empty<T>();
            }

            T? item = token.ToObject<T>();
            return item != null ? new T[] { item } : Array.Empty<T>(); ;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is List<T> list && list.Count == 1)
            {
                value = list[0];
            }

            serializer.Serialize(writer, value);
        }
    }
}
