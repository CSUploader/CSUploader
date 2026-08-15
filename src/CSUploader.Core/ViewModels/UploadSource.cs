// <copyright file="UploadSource.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// One thing the user added on the wizard's first step — a folder that was walked, or a single file
/// they picked. Shown in the Sources strip so the list of files has a visible provenance and each
/// addition can be taken back without clearing everything.
/// </summary>
public sealed partial class UploadSource : ObservableObject
{
    public UploadSource(string path, bool isFolder)
    {
        Path = path;
        IsFolder = isFolder;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The folder that was walked, or the file that was picked.</summary>
    public string Path { get; }

    public bool IsFolder { get; }

    /// <summary>What to show: the folder's own name (not the whole path, which is in the tooltip) or
    /// the file's name.</summary>
    public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : Path;

    /// <summary>How many files this source contributed — the ones NOT already in the list from an
    /// earlier source are what it can claim, so re-adding an overlapping folder reports honestly.</summary>
    [ObservableProperty]
    public partial int FileCount { get; set; }
}
