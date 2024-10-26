// <copyright file="PremiumPlan.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    public class PremiumPlan
    {
        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.GB })]
        [JsonProperty("storageLimit")]
        public ByteUnit StorageLimit { get; set; } = new(0);

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.GB })]
        [JsonProperty("dailyTrafficLimit")]
        public ByteUnit DailyTrafficLimit { get; set; } = new(0);

        [JsonProperty("plans")]
        public PaymentPlan[] PaymentPlans { get; set; } = Array.Empty<PaymentPlan>();

        [JsonProperty("upgrade")]
        public bool? Upgrade { get; set; }
    }
}
