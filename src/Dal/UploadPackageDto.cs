// <copyright file="UploadPackageDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Dal;

public class UploadPackageDto
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public DateTime CreatedDateTime { get; set; }

    public DateTime? ScheduledStartTime { get; set; }

    public bool IsCompleted { get; set; }

    public int? SpeedLimitKBps { get; set; }

    public UploadStartMode StartMode { get; set; }

    public bool IsRemovedFromUploads { get; set; }

    public ICollection<UploadPackageFileDto> Files { get; set; } = Array.Empty<UploadPackageFileDto>();
}
