// <copyright file="BearerTokenResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Auth
{
    // POST /v1/auth/token
    public class BearerTokenResponse
    {
        [JsonProperty("token_type")]
        public TokenType TokenType { get; set; }

        [JsonProperty("access_token")]
        public string? AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string? RefreshToken { get; set; }
    }
}
