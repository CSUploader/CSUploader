// <copyright file="FileEntry.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;

namespace CSUploader.ViewModels;

public partial class FileEntry : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string FullPath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>
    /// Which <see cref="UploadSource"/> put this file in the list, so removing that source removes
    /// exactly its files and leaves everything else alone. <see cref="Guid.Empty"/> for entries added
    /// before sources existed (nothing produces those today).
    /// </summary>
    public Guid SourceId { get; set; }
}
