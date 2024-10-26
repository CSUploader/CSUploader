// <copyright file="FileInfoResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    // GET /file?id=<id>
    public class FileInfoResponse
    {
        [JsonProperty("data")]
        public FileInfo? FileInfo { get; set; }
    }
}
