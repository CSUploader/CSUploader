// <copyright file="PaymentPackagesResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    // GET /payment/packages
    public class PaymentPackagesResponse
    {
        [JsonProperty("data")]
        public PaymentPackage[] PaymentPackages { get; set; } = Array.Empty<PaymentPackage>();
    }
}
