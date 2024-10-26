// <copyright file="DaysTimespanConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Extensions
{
    public class DaysTimespanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan ReadJson(JsonReader reader, Type objectType, TimeSpan existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.Value is string value)
            {
                if (double.TryParse(value, out double days))
                {
                    return TimeSpan.FromDays(days);
                }
            }

            try
            {
                double days = Convert.ToDouble(reader.Value);
                return TimeSpan.FromDays(days);
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
