// <copyright file="UpdateCheckResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>The kind of outcome an update check produced.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The check completed; no newer release is available.</summary>
    UpToDate,

    /// <summary>A newer release is available (<see cref="UpdateCheckResult.Info"/> is set).</summary>
    Available,

    /// <summary>The check could not complete (<see cref="UpdateCheckResult.FailureReason"/> is set).</summary>
    Failed,

    /// <summary>
    /// A newer release exists, but this process cannot install it: there is no Velopack layout
    /// around it (a loose build, or <c>dotnet run</c>). <see cref="UpdateCheckResult.NewVersion"/>
    /// is set and <see cref="UpdateCheckResult.Info"/> is deliberately NOT.
    /// <para>
    /// Separate from <see cref="Available"/> because the difference is not cosmetic. Everything that
    /// installs an update needs a Velopack <c>UpdateInfo</c> to pass back, and there is none here —
    /// Velopack's <c>DownloadUpdatesAsync</c> calls <c>EnsureInstalled</c> and throws
    /// <c>NotInstalledException</c>. Reporting this as <see cref="Available"/> would arm the install
    /// command with a payload that cannot exist.
    /// </para>
    /// </summary>
    AvailableNotInstallable,
}

/// <summary>
/// The outcome of an update check. Distinguishes a FAILED check from "no update available"
/// so callers can surface the failure instead of showing "you're on the latest version".
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateAvailableInfo? Info = null,
    string? FailureReason = null,
    string? NewVersion = null)
{
    /// <summary>A completed check with no newer release.</summary>
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate);

    /// <summary>A newer release is available.</summary>
    public static UpdateCheckResult Available(UpdateAvailableInfo info) => new(UpdateCheckStatus.Available, Info: info);

    /// <summary>
    /// A newer release exists that this build cannot install. Carries the version as a bare string
    /// rather than an <see cref="UpdateAvailableInfo"/>, because that type's <c>Payload</c> promises
    /// a Velopack <c>UpdateInfo</c> the install path can act on, and no such thing is obtainable
    /// without an installed layout.
    /// </summary>
    public static UpdateCheckResult AvailableNotInstallable(string newVersion)
        => new(UpdateCheckStatus.AvailableNotInstallable, NewVersion: newVersion);

    /// <summary>The check failed; <paramref name="reason"/> is a short human-readable message.</summary>
    public static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.Failed, FailureReason: reason);
}
