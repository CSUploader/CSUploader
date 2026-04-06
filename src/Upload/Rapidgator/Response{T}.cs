// <copyright file="Response{T}.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json.Serialization;

namespace CSUploader.Upload.Rapidgator;

public class Response<T> : Response
    where T : class
{
    [JsonPropertyName("response")]
    public T? Model { get; set; }
}
