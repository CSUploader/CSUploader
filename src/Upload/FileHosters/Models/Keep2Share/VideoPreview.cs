// <copyright file="VideoPreview.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    public class VideoPreview
    {
        [JsonProperty("disabled")]
        public bool? Disabled { get; set; }

        [JsonProperty("cover")]
        public string? CoverLink { get; set; }

        [JsonProperty("video")]
        public string? VideoLink { get; set; }

        [JsonProperty("duration")]
        public double? Duration { get; set; }
    }
}
