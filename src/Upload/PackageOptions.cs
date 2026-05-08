// <copyright file="PackageOptions.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader.Upload;

public class PackageOptions
{
    /// <summary>
    /// Gets or sets the application logger.
    /// </summary>
    public IAppLogger? Logger { get; set; }

    /// <summary>
    /// Gets or sets the application settings. Nullable — some code paths construct
    /// <see cref="PackageOptions"/> without DI (e.g. tests). Used by
    /// <see cref="Package.EffectiveSpeedLimitKBps"/> as the global speed-limit fallback.
    /// </summary>
    public AppSettings? Settings { get; init; }
    /// <summary>
    /// Gets or sets the directory path to the files.
    /// </summary>
    /// <value>
    /// The directory path to the files.
    /// </value>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Gets or sets a custom title for the package. When null, the directory name is used.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the selected files to include in the package.
    /// When null, all files in the directory are included.
    /// </summary>
    public List<string>? SelectedFiles { get; set; }

    /// <summary>
    /// Gets or sets the file hosters.
    /// </summary>
    /// <value>
    /// The file hosters.
    /// </value>
    public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosters { get; set; } = [];
}
