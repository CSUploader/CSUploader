// <copyright file="SecondsTimespanConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Extensions
{
    public class SecondsTimespanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan ReadJson(JsonReader reader, Type objectType, TimeSpan existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.Value is string value)
            {
                if (double.TryParse(value, out double seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }

            try
            {
                double seconds = Convert.ToDouble(reader.Value);
                return TimeSpan.FromSeconds(seconds);
            }
            catch
            {
                return existingValue;
            }
        }

        public override void WriteJson(JsonWriter writer, TimeSpan value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }
    }
}
