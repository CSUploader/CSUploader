// <copyright file="FileInfoResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.TezFiles
{
    // GET /v1/files/<id>
    public class FileInfoResponse
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("folderId")]
        public string? FolderId { get; set; }

        [JsonProperty("parentFolderId")]
        public string? ParentFolderId { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [JsonProperty("downloads")]
        public long? Downloads { get; set; }

        [JsonProperty("accessType")]
        public AccessType? AccessType { get; set; }

        [JsonProperty("isPublicFolder")]
        public bool? IsPublicFolder { get; set; }

        [JsonProperty("isDeleted")]
        public bool? IsDeleted { get; set; }

        [JsonProperty("isOwn")]
        public bool? IsOwn { get; set; }

        [JsonProperty("ownerId")]
        public long? OwnerId { get; set; }

        [JsonProperty("lastDownloadedAt")]
        public DateTime? LastDownloadedAt { get; set; }

        [JsonProperty("type")]
        public FileType? Type { get; set; }

        [JsonProperty("parent")]
        public FileInfoResponse? Parent { get; set; }

        [JsonProperty("malwareStatus")]
        public MalwareStatus? MalwareStatus { get; set; }

        [JsonProperty("contentType")]
        public string? ContentType { get; set; }

        [JsonProperty("hasAbuse")]
        public bool? HasAbuse { get; set; }

        [JsonConverter(typeof(ByteUnitJsonConverter), new[] { ByteUnitSymbol.B })]
        [JsonProperty("size")]
        public ByteUnit Size { get; set; } = new(0);

        [JsonProperty("videoInfo")]
        public VideoInfo? VideoInfo { get; set; }

        [JsonProperty("thumbnails")]
        public string[] ThumbnailLinks { get; set; } = Enumerable.Empty<string>().ToArray();

        [JsonProperty("videoPreview")]
        public VideoPreview? VideoPreview { get; set; }
    }
}
