// <copyright file="PackageCompressionOptions.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Compression;

namespace CSUploader.Upload;

public class PackageCompressionOptions
{
    public Compressor? Compressor { get; set; }

    /// <summary>
    /// Directory to save the compressed file(s) to.
    /// </summary>
    public string? OutputDirectoryPath { get; set; }

    /// <summary>
    /// Temporary directory to store the files when compressing.
    /// </summary>
    public string? TemporaryDirectory { get; set; }

    /// <summary>
    /// Archive password.
    /// </summary>
    public string? ArchivePassword { get; set; }
}
