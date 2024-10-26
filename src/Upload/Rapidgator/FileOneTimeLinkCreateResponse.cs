// <copyright file="FileOneTimeLinkCreateResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileOneTimeLinkCreateResponse
    {
        [JsonProperty("link")]
        public FileLink? Link { get; set; }
    }
}
