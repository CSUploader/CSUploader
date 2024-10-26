// <copyright file="Pager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Newtonsoft.Json;

namespace CSUploader.Upload.Rapidgator
{
    public class Pager
    {
        [JsonProperty("current")]
        public long Current { get; set; }

        [JsonProperty("total")]
        public long Total { get; set; }
    }
}
