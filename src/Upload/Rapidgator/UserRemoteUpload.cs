// <copyright file="UserRemoteUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class UserRemoteUpload
    {
        [JsonProperty("max_nb_jobs")]
        public int MaxNBJobs { get; set; }

        [JsonProperty("refresh_time")]
        public int RefreshTime { get; set; }
    }
}
