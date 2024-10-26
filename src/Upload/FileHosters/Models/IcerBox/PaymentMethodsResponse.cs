// <copyright file="PaymentMethodsResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    // GET /payment/methods
    public class PaymentMethodsResponse
    {
        [JsonProperty("data")]
        public PaymentMethod[] PaymentMethods { get; set; } = Array.Empty<PaymentMethod>();
    }
}
