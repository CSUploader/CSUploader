// <copyright file="UserUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class UserUpload
{
    [JsonPropertyName("max_file_size")]
    public long MaxFileSize { get; set; }

    [JsonPropertyName("nb_pipes")]
    public int NBPipes { get; set; }
}
