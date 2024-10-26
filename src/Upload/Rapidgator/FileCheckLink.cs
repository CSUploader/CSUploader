// <copyright file="FileCheckLink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileCheckLink
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("filename")]
        public string? Filename { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }
    }
}
