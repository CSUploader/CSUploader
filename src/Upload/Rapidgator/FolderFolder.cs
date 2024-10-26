// <copyright file="FolderFolder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FolderFolder
    {
        [JsonProperty("folder_id")]
        public int Id { get; set; }

        [JsonProperty("mode")]
        public int Mode { get; set; }

        [JsonProperty("mode_label")]
        public string? ModeLabel { get; set; }

        [JsonProperty("parent_folder_id")]
        public int? ParentFolderId { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("nb_folders")]
        public int? NBFolders { get; set; }

        [JsonProperty("nb_files")]
        public int? NBFiles { get; set; }

        [JsonProperty("size_files")]
        public int? SizeFiles { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("folders")]
        public FolderFolder[] Folders { get; set; } = Array.Empty<FolderFolder>();
    }
}
