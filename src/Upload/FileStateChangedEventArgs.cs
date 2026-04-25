// <copyright file="FileStateChangedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Event args raised when a <see cref="PackageFile"/>'s <see cref="FileState"/> changes.
/// </summary>
public class FileStateChangedEventArgs(PackageFile file, FileState oldState, FileState newState) : EventArgs
{
    /// <summary>
    /// Gets the file whose state changed.
    /// </summary>
    public PackageFile File { get; } = file;

    /// <summary>
    /// Gets the previous state.
    /// </summary>
    public FileState OldState { get; } = oldState;

    /// <summary>
    /// Gets the new state.
    /// </summary>
    public FileState NewState { get; } = newState;
}
