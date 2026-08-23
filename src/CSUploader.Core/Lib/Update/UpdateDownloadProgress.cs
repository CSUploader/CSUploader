// <copyright file="UpdateDownloadProgress.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// One tick of update-download progress: the percentage Velopack reported, plus the byte count, rate
/// and time remaining derived from it by <see cref="UpdateDownloadStats"/>.
/// </summary>
/// <param name="Percent">0-100, exactly as the updater reported it.</param>
/// <param name="BytesReceived">
/// Derived from <paramref name="Percent"/> and <paramref name="TotalBytes"/>, so it advances in
/// steps of one percent rather than continuously. Zero when the total is unknown.
/// </param>
/// <param name="TotalBytes">
/// The expected download size, or 0 when it could not be determined. An ESTIMATE: the updater
/// prefers delta packages and falls back to the full one on error, and which path it took is not
/// visible from outside.
/// </param>
/// <param name="BytesPerSecond">
/// A smoothed rate, or 0 before enough has arrived to measure one. Zero whenever
/// <paramref name="TotalBytes"/> is 0, since a byte rate cannot be derived from percentages alone.
/// </param>
/// <param name="Remaining">
/// Time left, or null before it can be estimated. Derived from the PERCENTAGE rate, not the byte
/// rate — the two are algebraically identical when the total is right, and this one stays correct
/// when it is wrong.
/// </param>
public readonly record struct UpdateDownloadProgress(
    int Percent,
    long BytesReceived,
    long TotalBytes,
    long BytesPerSecond,
    TimeSpan? Remaining)
{
    /// <summary>A tick carrying nothing but the percentage — the shape before any rate can be measured.</summary>
    public static UpdateDownloadProgress AtPercent(int percent) => new(percent, 0, 0, 0, null);

    /// <summary>Whether there is a byte count worth showing.</summary>
    public bool HasBytes => TotalBytes > 0;

    /// <summary>Whether there is a rate worth showing.</summary>
    public bool HasRate => BytesPerSecond > 0;
}
