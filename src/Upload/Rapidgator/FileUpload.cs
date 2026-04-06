// <copyright file="FileUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Extensions;
using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class FileUpload
{
    [JsonPropertyName("upload_id")]
    public string? UploadId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("file")]
    [JsonConverter(typeof(SingleOrArrayJsonConverter<FileFile>))]
    public FileFile[] File { get; set; } = [];

    [JsonPropertyName("state")]
    public int State { get; set; }

    [JsonPropertyName("state_label")]
    public string? StateLabel { get; set; }
}
