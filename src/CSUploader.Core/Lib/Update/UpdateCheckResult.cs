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

    /// <summary>Not running from a Velopack-installed location (loose build / dotnet run); nothing to check.</summary>
    NotInstalled,
}

/// <summary>
/// The outcome of an update check. Distinguishes a FAILED check from "no update available"
/// so callers can surface the failure instead of showing "you're on the latest version".
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateAvailableInfo? Info = null,
    string? FailureReason = null)
{
    /// <summary>A completed check with no newer release.</summary>
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate);

    /// <summary>A loose/non-installed build with nothing to check.</summary>
    public static UpdateCheckResult NotInstalled { get; } = new(UpdateCheckStatus.NotInstalled);

    /// <summary>A newer release is available.</summary>
    public static UpdateCheckResult Available(UpdateAvailableInfo info) => new(UpdateCheckStatus.Available, Info: info);

    /// <summary>The check failed; <paramref name="reason"/> is a short human-readable message.</summary>
    public static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.Failed, FailureReason: reason);
}
