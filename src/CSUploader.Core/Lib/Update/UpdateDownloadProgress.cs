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
/// Derived from <paramref name="Percent"/> through <see cref="UpdateDownloadPlan"/>, so it advances
/// in steps of one percent rather than continuously — and NOT by simple proportion, because the
/// percentage is not a byte fraction on the delta path. Zero when the total is unknown.
/// </param>
/// <param name="TotalBytes">
/// The expected download size, or 0 when it could not be determined. Chosen by mirroring the
/// updater's own delta-eligibility rules, so it is right about which package will be fetched — but
/// still an estimate, because a delta attempt that FAILS falls back to the full package mid-flight.
/// </param>
/// <param name="BytesPerSecond">
/// A smoothed rate, or 0 before enough has arrived to measure one. Zero whenever
/// <paramref name="TotalBytes"/> is 0, since a byte rate cannot be derived from percentages alone.
/// </param>
/// <param name="Remaining">
/// Time left, or null before it can be estimated. Derived from the PERCENTAGE rate rather than the
/// byte rate — not because a wrong total would distort it (the total cancels out of both forms, so
/// they agree), but because the size can be missing entirely, and a byte rate against no total is
/// zero, which would drop the estimate exactly when the percentage is all there is.
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
