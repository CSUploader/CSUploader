// <copyright file="PaymentPlan.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.TezFiles
{
    public class PaymentPlan
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonConverter(typeof(DaysTimespanConverter))]
        [JsonProperty("days")]
        public TimeSpan Days { get; set; }

        [JsonProperty("price")]
        public double? Price { get; set; }

        [JsonProperty("paymentSystems")]
        public PaymentMethod[] PaymentMethods { get; set; } = Array.Empty<PaymentMethod>();
    }
}
