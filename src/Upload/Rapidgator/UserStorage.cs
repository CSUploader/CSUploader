// <copyright file="UserStorage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class UserStorage
    {
        [JsonProperty("total")]
        public long? Total { get; set; }

        [JsonProperty("left")]
        public long? Left { get; set; }
    }
}
