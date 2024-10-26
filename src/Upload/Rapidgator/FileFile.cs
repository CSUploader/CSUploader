// <copyright file="FileFile.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileFile
    {
        [JsonProperty("file_id")]
        public string? FileId { get; set; }

        [JsonProperty("mode")]
        public int Mode { get; set; }

        [JsonProperty("mode_label")]
        public string? ModeLabel { get; set; }

        [JsonProperty("folder_id")]
        public string? FolderId { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("hash")]
        public string? Hash { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }
    }
}
