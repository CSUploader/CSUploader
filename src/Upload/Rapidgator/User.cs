// <copyright file="User.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class User
    {
        [JsonProperty("email")]
        public string? Email { get; set; }

        [JsonProperty("is_premium")]
        public bool IsPremium { get; set; }

        [JsonProperty("premium_end_time")]
        public DateTime? PremiumEndTime { get; set; }

        [JsonProperty("state")]
        public int State { get; set; }

        [JsonProperty("state_label")]
        public string? StateLabel { get; set; }

        [JsonProperty("traffic")]
        public UserTraffic? Traffic { get; set; }

        [JsonProperty("storage")]
        public UserStorage? Storage { get; set; }

        [JsonProperty("upload")]
        public UserUpload? Upload { get; set; }
    }
}
