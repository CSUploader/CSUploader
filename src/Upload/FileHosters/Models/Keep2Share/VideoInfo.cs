// <copyright file="VideoInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    public class VideoInfo
    {
        [JsonProperty("duration")]
        public double? Duration { get; set; }

        [JsonProperty("resolution")]
        public VideoResolution Resolution { get; set; } = new();

        [JsonProperty("format")]
        public string? Format { get; set; }
    }
}
