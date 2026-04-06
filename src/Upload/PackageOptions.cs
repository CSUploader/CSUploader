// <copyright file="PackageOptions.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

namespace CSUploader.Upload;

public class PackageOptions
{
    /// <summary>
    /// Gets or sets the directory path to the files.
    /// </summary>
    /// <value>
    /// The directory path to the files.
    /// </value>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Gets or sets the compression options.
    /// </summary>
    /// <value>
    /// The compression options.
    /// </value>
    public PackageCompressionOptions CompressionOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the file hosters.
    /// </summary>
    /// <value>
    /// The file hosters.
    /// </value>
    public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosters { get; set; } = [];
}
