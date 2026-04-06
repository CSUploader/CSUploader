// <copyright file="UploadGroupJob.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Compression.ZevenZip;

namespace CSUploader.Upload;

public class UploadGroupJob
{
    public string? InputDirectoryPath { get; set; }

    public ZevenZip.CompressionOptions CompressionOptions { get; set; } = new();

    public string? OutputFilePath { get; set; }

    public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosters { get; set; } = [];
}
