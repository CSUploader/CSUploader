// <copyright file="PaymentMethod.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    public class PaymentMethod
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("descriptor")]
        public string? Descriptor { get; set; }

        [JsonProperty("Logo")]
        public string? Logo { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("txn_fee")]
        public long? TaxFee { get; set; }

        [JsonProperty("currency")]
        public Currency Currency { get; set; }

        [JsonProperty("type")]
        public PaymentType Type { get; set; }

        [JsonProperty("hide_packages")]
        public int[] HidePackages { get; set; } = Array.Empty<int>();

        [JsonProperty("base_url")]
        public string? BaseUrl { get; set; }

        [JsonProperty("base_number")]
        public int? BaseNumber { get; set; }
    }
}
