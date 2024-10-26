// <copyright file="PaymentMethod.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    public class PaymentMethod
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("position")]
        public long Position { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }
    }
}
