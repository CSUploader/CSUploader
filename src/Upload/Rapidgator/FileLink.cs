// <copyright file="FileLink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileLink
    {
        [JsonProperty("link_id")]
        public string? LinkId { get; set; }

        [JsonProperty("file")]
        public FileFile? File { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("state")]
        public string? State { get; set; }

        [JsonProperty("state_label")]
        public string? StateLabel { get; set; }

        [JsonProperty("callback_url")]
        public string? CallbackUrl { get; set; }

        [JsonProperty("notify")]
        public bool Notify { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("downloaded")]
        public bool Downloaded { get; set; }
    }
}
