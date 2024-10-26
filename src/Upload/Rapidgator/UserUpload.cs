// <copyright file="UserUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class UserUpload
    {
        [JsonProperty("max_file_size")]
        public long MaxFileSize { get; set; }

        [JsonProperty("nb_pipes")]
        public int NBPipes { get; set; }
    }
}
