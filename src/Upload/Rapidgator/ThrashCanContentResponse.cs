// <copyright file="ThrashCanContentResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class ThrashCanContentResponse
    {
        [JsonProperty("files")]
        public ThrashCanFile[] Files { get; set; } = Array.Empty<ThrashCanFile>();

        [JsonProperty("pager")]
        public Pager? Pager { get; set; }
    }
}
