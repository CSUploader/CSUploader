// <copyright file="FileOneTimeLinkInfoResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class FileOneTimeLinkInfoResponse
    {
        [JsonProperty("links")]
        public FileLink[] Links { get; set; } = Array.Empty<FileLink>();
    }
}
