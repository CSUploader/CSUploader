// <copyright file="UserTraffic.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class UserTraffic
{
    [JsonPropertyName("total")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Total { get; set; }

    [JsonPropertyName("left")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Left { get; set; }
}
