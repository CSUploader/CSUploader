// <copyright file="UserInfoResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

// POST /api/v2/user/info?token=arsd4h37ecfkdqd16uolf5nai1
public class UserInfoResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("user")]
    public User? User { get; set; }
}
