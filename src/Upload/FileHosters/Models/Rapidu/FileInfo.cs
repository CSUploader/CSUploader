// <copyright file="FileInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Rapidu
{
    public class FileInfo
    {
        [JsonProperty("fileStatus")]
        public FileStatus Status { get; set; }

        [JsonProperty("fileId")]
        public long? Id { get; set; }

        [JsonProperty("fileName")]
        public string? Name { get; set; }

        [JsonProperty("fileDesc")]
        public string? Description { get; set; }

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("fileSize")]
        public ByteUnit Size { get; set; } = new(0);

        [JsonProperty("fileUrl")]
        public string? Url { get; set; }
    }
}
