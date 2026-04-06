// <copyright file="FileHosterLoginDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Dal;

public class FileHosterLoginDto
{
    public int Id { get; set; }

    public string? FileHosterName { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool Disabled { get; set; }

    public AccountType AccountType { get; set; }
}
