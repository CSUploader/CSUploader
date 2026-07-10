// <copyright file="RemoveFinishedUploadsMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Auto-removal policy for the Uploads tab. Mirrors JDownloader 2's "Remove finished
/// downloads" dropdown — a single setting subsumes the per-file / per-package /
/// at-startup variants. Persisted history on the Uploaded tab is unaffected; this only
/// controls when the row disappears from the in-progress Uploads grid.
/// </summary>
public enum RemoveFinishedUploadsMode
{
    /// <summary>Keep finished entries on the Uploads tab indefinitely.</summary>
    Never,

    /// <summary>Remove a file from the Uploads tab the moment it completes successfully.</summary>
    Immediately,

    /// <summary>At app launch, soft-remove any package whose files all completed successfully.</summary>
    AtStartup,

    /// <summary>Soft-remove a package once every file in it has completed successfully.</summary>
    WhenPackageIsReady,
}
