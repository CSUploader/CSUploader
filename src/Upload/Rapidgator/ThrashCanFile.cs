// <copyright file="ThrashCanFile.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class ThrashCanFile : FileFile
    {
        [JsonProperty("nb_downloads")]
        public int NBDownloads { get; set; }
    }
}
