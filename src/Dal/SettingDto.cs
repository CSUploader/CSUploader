// <copyright file="SettingDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class SettingDto
{
    public int Id { get; set; }

    public string? Key { get; set; }

    public string? Value { get; set; }
}
