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
    /// Gets or sets the title for the package.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Full paths of files to include in the package. When null or empty, no files are added
    /// (the wizard always populates this for both Directory and Files modes).
    /// </summary>
    public List<string>? SelectedFiles { get; set; }

    /// <summary>
    /// Gets or sets the file hosters.
    /// </summary>
    /// <value>
    /// The file hosters.
    /// </value>
    public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosters { get; set; } = [];

    /// <summary>
    /// Optional per-hoster file allow-list, keyed by hoster name (matching
    /// <see cref="FileHosterClient.Name"/>); each value is the set of full file paths to upload
    /// to that hoster. When an entry is present for a hoster,
    /// <see cref="Package.AddPackageFiles(Pipeline.IFileHosterRegistry?, IAppLogger?)"/> only
    /// creates (file, hoster) pairs whose path is in that set — this is how the upload wizard's
    /// Summary page sends each hoster the subset of files the user kept after the per-hoster
    /// available-space fit. A hoster with no entry (or a null map) stays unrestricted (every
    /// selected file), preserving the default cross-product for non-wizard callers. It only ever
    /// RESTRICTS further; the per-file size and storage-quota filters still apply on top.
    /// <para>Paths are matched against <see cref="SelectedFiles"/> using whatever comparer the
    /// supplied <see cref="HashSet{T}"/> uses, so callers must populate it with the same path
    /// strings/casing as <see cref="SelectedFiles"/> (the wizard sources both from the same
    /// <c>FileEntry.FullPath</c>); use an <see cref="StringComparer.OrdinalIgnoreCase"/> set if a
    /// caller's two sides might ever differ in case on Windows' case-insensitive filesystem.</para>
    /// </summary>
    public Dictionary<string, HashSet<string>>? IncludedFilesPerHoster { get; set; }
}
