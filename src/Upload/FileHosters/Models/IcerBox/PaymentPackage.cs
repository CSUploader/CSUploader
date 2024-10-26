// <copyright file="PaymentPackage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using CSUploader.Lib;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    public class PaymentPackage
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonConverter(typeof(DaysTimespanConverter))]
        [JsonProperty("duration")]
        public TimeSpan? Duration { get; set; }

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("storage")]
        public ByteUnit Storage { get; set; } = new(0);

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("bandwidth")]
        public ByteUnit Bandwidth { get; set; } = new(0);

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("volume")]
        public ByteUnit Volume { get; set; } = new(0);

        [JsonProperty("price")]
        public Dictionary<string, long> Price { get; set; } = new();

        [JsonProperty("original_price")]
        public long? OriginalPrice { get; set; }
    }
}
