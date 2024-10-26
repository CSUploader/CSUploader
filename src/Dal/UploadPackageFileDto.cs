// <copyright file="UploadPackageFileDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class UploadPackageFileDto
    {
        public int Id { get; set; }

        public string? FileName { get; set; }

        public string? FileDirectory { get; set; }

        public long FileSize { get; set; }

        public string? FileHoster { get; set; }

        public DateTime StartDateTime { get; set; }

        public DateTime FinishedDateTime { get; set; }

        public string? CompressionPassword { get; set; }

        public string? FileUrl { get; set; }

        public string? FileHosterName { get; set; }

        public UploadPackageDto? Package { get; set; }
    }
}
