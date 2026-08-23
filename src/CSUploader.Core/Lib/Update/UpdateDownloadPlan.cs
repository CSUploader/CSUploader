// <copyright file="UpdateDownloadPlan.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// What the updater is going to download, when that can be said at all.
/// <para>
/// There are two answers and no third. Either one package is being fetched whole, in which case the
/// reported percentage IS its byte fraction and a size can be counted against it — or the updater is
/// applying deltas, in which case no byte figure can honestly be derived and this says so.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the delta path has no size.</b> Velopack reports one percentage for the whole operation.
/// On the delta path that operation downloads the deltas CONCURRENTLY — <c>ForEachAsync</c> with a
/// default degree of parallelism of four — and sums each one's count-weighted contribution into a
/// single number, then spends the last thirty points of it patching, which moves no bytes at all.
/// Two deltas of 900 and 100 bytes both half-finished report the same 35% as the first one being
/// wholly finished, and 500 bytes have arrived in one case and 900 in the other. The information is
/// not in the callback, so no arithmetic recovers it.
/// </para>
/// <para>
/// An earlier version of this file modelled sequential bands over 0-70 and inverted them. It was
/// wrong for exactly that reason. The bar, the percentage and the countdown all still work on the
/// delta path — only the byte figures go away, which is the honest outcome.
/// </para>
/// </remarks>
public sealed class UpdateDownloadPlan
{
    private UpdateDownloadPlan(long totalBytes) => TotalBytes = totalBytes;

    /// <summary>
    /// No size can be counted against the percentage — either nothing advertised one, or the
    /// updater is taking the delta path, where the percentage is not a byte fraction.
    /// </summary>
    public static UpdateDownloadPlan Unknown { get; } = new(0);

    /// <summary>Whether there is a size worth showing.</summary>
    public bool IsKnown => TotalBytes > 0;

    /// <summary>Bytes the download is expected to move; 0 when unknown.</summary>
    public long TotalBytes { get; }

    /// <summary>
    /// One package, fetched whole. Velopack passes that download's own progress straight through, so
    /// the percentage really is the byte fraction.
    /// </summary>
    public static UpdateDownloadPlan Full(long bytes) => bytes > 0 ? new(bytes) : Unknown;

    /// <summary>How many bytes have arrived by the time the updater reports <paramref name="percent"/>.</summary>
    public long BytesAt(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return IsKnown ? (long)(percent / 100.0 * TotalBytes) : 0;
    }
}
