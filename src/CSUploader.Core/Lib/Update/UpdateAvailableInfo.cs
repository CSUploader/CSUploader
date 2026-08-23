// <copyright file="UpdateAvailableInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// VM-facing summary of an available update. Wraps Velopack's <c>UpdateInfo</c> so the
/// rest of the app doesn't take a hard dependency on Velopack types.
/// </summary>
public sealed class UpdateAvailableInfo(string newVersion, object payload, UpdateDownloadPlan downloadPlan)
{
    public string NewVersion { get; } = newVersion;

    /// <summary>
    /// Whether a size can be counted against the download's percentage, and if so which. Required
    /// rather than defaulted: an update built without one would silently lose its byte readout, and
    /// a compiler error is a better way to find that out than a missing line on screen.
    /// </summary>
    public UpdateDownloadPlan DownloadPlan { get; } = downloadPlan;

    /// <summary>
    /// Opaque payload — the underlying <c>Velopack.UpdateInfo</c>. Passed back to
    /// <see cref="IUpdateService.DownloadAsync"/> and <see cref="IUpdateService.ApplyAndRestart"/>.
    /// </summary>
    public object Payload { get; } = payload;
}
