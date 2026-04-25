// <copyright file="UpdateAvailableInfo.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// VM-facing summary of an available update. Wraps Velopack's <c>UpdateInfo</c> so the
/// rest of the app doesn't take a hard dependency on Velopack types.
/// </summary>
public sealed class UpdateAvailableInfo
{
    public UpdateAvailableInfo(string newVersion, object payload)
    {
        NewVersion = newVersion;
        Payload = payload;
    }

    public string NewVersion { get; }

    /// <summary>
    /// Opaque payload — the underlying <c>Velopack.UpdateInfo</c>. Passed back to
    /// <see cref="IUpdateService.DownloadAsync"/> and <see cref="IUpdateService.ApplyAndRestart"/>.
    /// </summary>
    public object Payload { get; }
}
