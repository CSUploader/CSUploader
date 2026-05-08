// <copyright file="AutostartUploadsMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Whether (and when) CSUploader should auto-start pending uploads at app launch
/// without user interaction. Mirrors JDownloader 2's "Autostart downloads" dropdown.
/// </summary>
public enum AutostartUploadsMode
{
    /// <summary>Never auto-start. The user has to click Start on each package.</summary>
    Never,

    /// <summary>
    /// Only auto-start if uploads were active at last session's end — i.e. the package
    /// had at least one file in a non-paused/cancelled/terminal state when the app shut
    /// down. Matches the JDownloader 2 default.
    /// </summary>
    OnlyIfRunningAtLastSession,

    /// <summary>Always auto-start any package with pending files at launch.</summary>
    Always,
}
