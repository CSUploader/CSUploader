// <copyright file="PackageAddedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Event args for when packages or package files are added.
/// </summary>
public class PackageAddedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PackageAddedEventArgs"/> class.
    /// </summary>
    /// <param name="parentPackage">The parent package, or null for top-level additions.</param>
    /// <param name="packages">The packages that were added.</param>
    public PackageAddedEventArgs(Package? parentPackage, Package[] packages)
    {
        ParentPackage = parentPackage;
        Packages = packages;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageAddedEventArgs"/> class
    /// for package file additions.
    /// </summary>
    /// <param name="parentPackage">The parent package.</param>
    /// <param name="packageFiles">The package files that were added.</param>
    public PackageAddedEventArgs(Package parentPackage, PackageFile[] packageFiles)
    {
        ParentPackage = parentPackage;
        PackageFiles = packageFiles;
    }

    /// <summary>
    /// Gets the parent package, or null for top-level additions.
    /// </summary>
    public Package? ParentPackage { get; }

    /// <summary>
    /// Gets the packages that were added, or null if files were added.
    /// </summary>
    public Package[]? Packages { get; }

    /// <summary>
    /// Gets the package files that were added, or null if packages were added.
    /// </summary>
    public PackageFile[]? PackageFiles { get; }
}
