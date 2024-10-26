// <copyright file="DownloadTimeResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    // GET /v1/files/<id>/download-time
    public class DownloadTimeResponse
    {
        [JsonConverter(typeof(SecondsTimespanConverter))]
        [JsonProperty("free")]
        public TimeSpan? FreeUsers { get; set; }

        [JsonConverter(typeof(SecondsTimespanConverter))]
        [JsonProperty("premium")]
        public TimeSpan? PremiumUsers { get; set; }
    }
}
