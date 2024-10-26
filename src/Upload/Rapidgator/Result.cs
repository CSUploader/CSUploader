// <copyright file="Result.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class Result
    {
        [JsonProperty("success")]
        public int Success { get; set; }

        [JsonProperty("success_ids")]
        public int[] SuccesIds { get; set; } = Array.Empty<int>();

        [JsonProperty("fail")]
        public int Fail { get; set; }

        [JsonProperty("fail_ids")]
        public int[] FailIds { get; set; } = Array.Empty<int>();

        [JsonProperty("errors")]
        public string[] Errors { get; set; } = Array.Empty<string>();
    }
}
