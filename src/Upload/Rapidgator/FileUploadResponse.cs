// <copyright file="FileUploadResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class FileUploadResponse
{
    [JsonPropertyName("upload")]
    public FileUpload? Upload { get; set; }
}