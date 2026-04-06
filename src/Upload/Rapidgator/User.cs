// <copyright file="User.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class User
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("premium_end_time")]
    public DateTime? PremiumEndTime { get; set; }

    [JsonPropertyName("state")]
    public int State { get; set; }

    [JsonPropertyName("state_label")]
    public string? StateLabel { get; set; }

    [JsonPropertyName("traffic")]
    public UserTraffic? Traffic { get; set; }

    [JsonPropertyName("storage")]
    public UserStorage? Storage { get; set; }

    [JsonPropertyName("upload")]
    public UserUpload? Upload { get; set; }
}
