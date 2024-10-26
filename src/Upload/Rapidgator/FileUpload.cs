// <copyright file="FileUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileUpload
    {
        [JsonProperty("upload_id")]
        public string? UploadId { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("file")]
        [JsonConverter(typeof(SingleOrArrayJsonConverter<FileFile>))]
        public FileFile[] File { get; set; } = Array.Empty<FileFile>();

        [JsonProperty("state")]
        public int State { get; set; }

        [JsonProperty("state_label")]
        public string? StateLabel { get; set; }
    }
}
