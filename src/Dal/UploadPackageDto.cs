// <copyright file="UploadPackageDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class UploadPackageDto
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public ICollection<UploadPackageFileDto> Files { get; set; } = Array.Empty<UploadPackageFileDto>();
}
