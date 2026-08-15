// <copyright file="SummaryFileItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// One file row inside a <see cref="HosterUploadSummary"/> on the wizard's Summary page. Its
/// <see cref="Included"/> checkbox is INDEPENDENT per hoster — unchecking a file for one hoster
/// doesn't affect another hoster's copy or the Page 1 selection — which is how the per-hoster
/// available-space fit lets a big file go to a roomy hoster but not a tight one.
/// </summary>
public sealed partial class SummaryFileItem : ObservableObject
{
    [ObservableProperty]
    public partial bool Included { get; set; }

    public SummaryFileItem(FileEntry file, bool included)
    {
        File = file;
        Included = included;
    }

    public FileEntry File { get; }

    public string FileName => File.FileName;

    public long Size => File.Size;

    /// <summary>True when the Summary step's capacity auto-fit unchecked THIS file to keep the hoster within
    /// its available space — as opposed to the user unchecking it by hand. Only auto-evicted files count
    /// toward the "N unchecked to fit the available space" notices; a manual toggle clears this (a file the
    /// user unchecks isn't a space eviction). Plain field — not observable; the notices recompute on the
    /// Included change that always accompanies it.</summary>
    public bool AutoUncheckedForSpace { get; set; }
}
