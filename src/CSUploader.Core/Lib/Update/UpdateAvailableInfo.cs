// <copyright file="UpdateAvailableInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// VM-facing summary of an available update. Wraps Velopack's <c>UpdateInfo</c> so the
/// rest of the app doesn't take a hard dependency on Velopack types.
/// </summary>
public sealed class UpdateAvailableInfo(string newVersion, object payload, long downloadBytes = 0)
{
    public string NewVersion { get; } = newVersion;

    /// <summary>
    /// Roughly how many bytes the download will move, or 0 if it could not be worked out.
    /// <para>
    /// An ESTIMATE, and unavoidably so. The updater applies delta packages when it can and falls
    /// back to the full one on error, without saying which it did — so this is the delta total when
    /// deltas exist and the full package otherwise, and a fallback makes it wrong. Fine for a
    /// progress readout; not a figure to make a decision on.
    /// </para>
    /// </summary>
    public long DownloadBytes { get; } = downloadBytes;

    /// <summary>
    /// Opaque payload — the underlying <c>Velopack.UpdateInfo</c>. Passed back to
    /// <see cref="IUpdateService.DownloadAsync"/> and <see cref="IUpdateService.ApplyAndRestart"/>.
    /// </summary>
    public object Payload { get; } = payload;
}
