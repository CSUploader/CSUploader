// <copyright file="FileDownloadResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileDownloadResponse
    {
        [JsonProperty("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonProperty("delay")]
        public int Delay { get; set; }
    }
}
