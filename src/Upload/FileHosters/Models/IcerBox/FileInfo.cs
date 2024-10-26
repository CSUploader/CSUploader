// <copyright file="FileInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.IcerBox
{
    public class FileInfo
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("size")]
        public ByteUnit Size { get; set; } = new(0);

        [JsonProperty("status")]
        public FileStatus Status { get; set; }

        [JsonProperty("free_available")]
        public bool FreeAvailable { get; set; }

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.kB })]
        [JsonProperty("free_speed")]
        public ByteUnit FreeSpeed { get; set; } = new(0);

        [JsonProperty("md5")]
        public string? MD5 { get; set; }

        [JsonProperty("password")]
        public bool Password { get; set; }
    }
}
