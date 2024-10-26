// <copyright file="FileHosterFileInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Upload
{
    public class FileHosterFileInfo
    {
        public FileHosterFileInfo()
        {
        }

        public FileHosterFileInfo(string id, FileHosterFileStatus fileStatus, string fileName, ByteUnit fileSize, string url)
        {
            Id = id;
            FileStatus = fileStatus;
            FileName = fileName;
            FileSize = fileSize;
            Url = url;
        }

        public FileHosterFileInfo(string id, FileHosterFileStatus fileStatus, string fileName, ByteUnit fileSize, string checksum, string url)
            : this(id, fileStatus, fileName, fileSize, url)
        {
            Checksum = checksum;
        }

        public string? Id { get; set; }

        public FileHosterFileStatus FileStatus { get; set; } = FileHosterFileStatus.Unknown;

        public string? FileName { get; set; }

        public ByteUnit FileSize { get; set; } = new(0);

        public string? Checksum { get; set; }

        public string? Url { get; set; }
    }
}
