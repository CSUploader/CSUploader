// <copyright file="ByteUnitJsonConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Lib
{
    public class ByteUnitJsonConverter : JsonConverter
    {
        public ByteUnitJsonConverter()
        {
        }

        public ByteUnitJsonConverter(object parameters)
        {
            switch (parameters)
            {
                case ByteUnitSymbol[] byteUnitSymbols:
                    ByteUnitSymbol = byteUnitSymbols.First();
                    break;

                case ByteUnitPrefix[] byteUnitPrefixes:
                    ByteUnitPrefix = byteUnitPrefixes.First();
                    break;
            }
        }

        private ByteUnitSymbol? ByteUnitSymbol { get; set; }

        private ByteUnitPrefix? ByteUnitPrefix { get; set; }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.Value is ByteUnit byteUnit)
            {
                return byteUnit;
            }

            if (reader.Value is string value)
            {
                if (ByteUnit.TryParseSize(value, out ByteUnit? byteUnit2))
                {
                    return byteUnit2;
                }
            }

            try
            {
                double bytes = Convert.ToDouble(reader.Value);
                if (ByteUnitSymbol.HasValue)
                {
                    return new ByteUnit(bytes, ByteUnitSymbol.Value);
                }

                if (ByteUnitPrefix.HasValue)
                {
                    return new ByteUnit(bytes, ByteUnitPrefix.Value);
                }

                return new ByteUnit(bytes, Lib.ByteUnitSymbol.B);
            }
            catch
            {
                return existingValue;
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is ByteUnit byteUnit)
            {
                writer.WriteValue(byteUnit.ToFriendlyString());
            }
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(ByteUnit) ||
                objectType == typeof(double) ||
                objectType == typeof(double?) ||
                objectType == typeof(float) ||
                objectType == typeof(float?) ||
                objectType == typeof(long) ||
                objectType == typeof(long?) ||
                objectType == typeof(ulong) ||
                objectType == typeof(ulong?) ||
                objectType == typeof(int) ||
                objectType == typeof(int?) ||
                objectType == typeof(uint) ||
                objectType == typeof(uint?) ||
                objectType == typeof(short) ||
                objectType == typeof(short?) ||
                objectType == typeof(ushort) ||
                objectType == typeof(ushort?) ||
                objectType == typeof(byte) ||
                objectType == typeof(byte?) ||
                objectType == typeof(sbyte) ||
                objectType == typeof(sbyte?);
            }
    }
}
