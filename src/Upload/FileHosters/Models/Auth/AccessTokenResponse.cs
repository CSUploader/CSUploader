// <copyright file="AccessTokenResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using Newtonsoft.Json;

namespace CSUploader.Upload.FileHosters.Models.Auth
{
    // JSON Web Token (JWT)
    // GET /v1/auth/token
    public class AccessTokenResponse
    {
        // optional
        [JsonProperty("iss")]
        public string? Issuer { get; set; }

        // optional
        [JsonProperty("sub")]
        public string? Subject { get; set; }

        // optional
        [JsonProperty("aud")]
        public string? Audience { get; set; }

        // optional
        [JsonConverter(typeof(SecondsEpochConverter))]
        [JsonProperty("exp")]
        public DateTime ExpirationTime { get; set; }

        // optional
        [JsonConverter(typeof(SecondsEpochConverter))]
        [JsonProperty("nbf")]
        public DateTime NotBefore { get; set; }

        // optional
        [JsonConverter(typeof(SecondsEpochConverter))]
        [JsonProperty("iat")]
        public DateTime IssuedAt { get; set; }

        // optional
        [JsonProperty("jti")]
        public string? JWTId { get; set; }

        // Custom public claim names (used by i.e. keep2share and fileboom)
        [JsonProperty("type")]
        public TokenType? Type { get; set; }

        [JsonProperty("ownerId")]
        public string? OwnerId { get; set; }

        [JsonConverter(typeof(SecondsEpochConverter))]
        [JsonProperty("expiredAt")]
        private DateTime _expiredAt
        {
            set { ExpirationTime = value; }
        }

        [JsonConverter(typeof(SecondsEpochConverter))]
        [JsonProperty("issuedAt")]
        private DateTime _issuedAt
        {
            set { IssuedAt = value; }
        }

        [JsonProperty("issuer")]
        private string? _issuer
        {
            set { Issuer = value; }
        }
    }
}
