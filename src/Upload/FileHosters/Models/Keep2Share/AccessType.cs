// <copyright file="AccessType.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CSUploader.Upload.FileHosters.Models.Keep2Share
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AccessType
    {
        [EnumMember(Value = "premium")]
        PremiumOnly,

        [EnumMember(Value = "public")]
        Free
    }
}
