// <copyright file="BrowseStartMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Where the upload wizard's "Add files…" / "Add folder…" pickers open. Before this the folder
/// picker started at the last folder added IN THAT WIZARD SESSION and the file picker started
/// nowhere at all, so every fresh wizard began at whatever the OS considered home.
/// </summary>
public enum BrowseStartMode
{
    /// <summary>
    /// Reopen where the last pick was made, remembered across restarts
    /// (<see cref="AppSettings.LastBrowsedFolder"/>). The default: it needs no configuring and is
    /// right for the common case of adding several things from one place over several sittings.
    /// </summary>
    LastUsed,

    /// <summary>
    /// Always open at <see cref="AppSettings.BrowseStartFolder"/> — for a fixed staging directory
    /// that everything is uploaded from. Falls back to the OS default when the path is blank.
    /// </summary>
    FixedFolder,

    /// <summary>Suggest nothing and let the OS picker decide, as it did before this setting existed.</summary>
    SystemDefault,
}
