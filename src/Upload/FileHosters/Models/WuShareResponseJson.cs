// <copyright file="WuShareResponseJson.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models
{
    public class WuShareResponseJson
    {
        [JsonProperty("status")]
        public string? Status { get; set; }
    }
}
