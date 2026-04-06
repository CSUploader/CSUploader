// <copyright file="FolderFolder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class FolderFolder
{
    [JsonPropertyName("folder_id")]
    public int Id { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("mode_label")]
    public string? ModeLabel { get; set; }

    [JsonPropertyName("parent_folder_id")]
    public int? ParentFolderId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("nb_folders")]
    public int? NBFolders { get; set; }

    [JsonPropertyName("nb_files")]
    public int? NBFiles { get; set; }

    [JsonPropertyName("size_files")]
    public int? SizeFiles { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("folders")]
    public FolderFolder[] Folders { get; set; } = [];
}
