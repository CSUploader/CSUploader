// <copyright file="JsonHelpers.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSUploader.Lib.Extensions;

public static class JsonHelpers
{
    // Rapidgator's API returns numeric IDs as JSON strings on some endpoints
    // (folder/create returns "folder_id":"8676913") and as numbers on others.
    // AllowReadingFromString accepts both shapes for any int/long/double property
    // so a single DTO works against either response form.
    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static bool TryDeserializeObject<T>(string value, [NotNullWhen(true)] out T? result)
        where T : class, new()
    {
        try
        {
            result = JsonSerializer.Deserialize<T>(value, Options);

            return result != null;
        }
        catch
        {
            result = null;

            return false;
        }
    }
}
