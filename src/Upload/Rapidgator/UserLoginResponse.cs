// <copyright file="UserLoginResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    // POST /api/v2/user/login?login=user@email.com&password=pass
    public class UserLoginResponse
    {
        [JsonProperty("token")]
        public string? Token { get; set; }

        [JsonProperty("user")]
        public User? User { get; set; }
    }
}
