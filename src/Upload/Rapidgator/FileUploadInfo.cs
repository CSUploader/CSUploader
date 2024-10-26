// <copyright file="FileUploadInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileUploadInfo
    {
        [JsonProperty("upload_id")]
        public string? UploadId { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("file")]
        public FileFile? File { get; set; }

        [JsonProperty("state")]
        public int State { get; set; }

        [JsonProperty("state_label")]
        public string? StateLabel { get; set; }
    }
}
