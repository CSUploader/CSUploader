// <copyright file="SecondsEpochConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CSUploader.Extensions
{
    public class SecondsEpochConverter : DateTimeConverterBase
    {
        private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            try
            {
                double seconds = Convert.ToDouble(reader.Value);
                return Epoch.AddSeconds(seconds);
            }
            catch
            {
                return existingValue;
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value != null)
            {
                writer.WriteRawValue(((DateTime)value - Epoch).TotalSeconds.ToString());
            }
        }
    }
}
