// <copyright file="UpdateDownloadPlan.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// What the updater is going to download, and how its percentage maps onto bytes.
/// <para>
/// The percentage is NOT a byte fraction. Velopack reports one number for the whole operation, and
/// on the delta path that operation is "download every delta, then patch" — so the percentage runs
/// through the downloads and then through work that moves no bytes at all. Reading it as a byte
/// fraction reports a download that is still arriving long after it finished.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The mapping below is Velopack 1.2.0's, read from
/// <c>UpdateManager.DownloadAndApplyDeltaUpdates</c>, not inferred from its behaviour.</b> The delta
/// downloads occupy 0-<see cref="DeltaDownloadCeiling"/>, each delta taking an EQUAL share of that
/// range regardless of its size (Velopack divides by the delta count), and the remainder is the
/// patch process, which reports nothing until it is done.
/// </para>
/// <para>
/// That coupling is the price of showing byte counts at all, and it is contained here: if a future
/// Velopack changes the split, this is the one place that is wrong, the tests that pin it fail, and
/// the fallback is <see cref="Unknown"/> — a bar and a countdown, which is what the window showed
/// before any of this existed.
/// </para>
/// </remarks>
public sealed class UpdateDownloadPlan
{
    /// <summary>Where Velopack's delta phase stops and its patch phase begins.</summary>
    internal const int DeltaDownloadCeiling = 70;

    private readonly long[] _deltaSizes;
    private readonly long[] _cumulativeBefore;

    private UpdateDownloadPlan(long totalBytes, long[] deltaSizes, long[] cumulativeBefore)
    {
        TotalBytes = totalBytes;
        _deltaSizes = deltaSizes;
        _cumulativeBefore = cumulativeBefore;
    }

    /// <summary>The size could not be worked out — the window shows a bar and a countdown only.</summary>
    public static UpdateDownloadPlan Unknown { get; } = new(0, [], []);

    /// <summary>Whether there is a size worth showing.</summary>
    public bool IsKnown => TotalBytes > 0;

    /// <summary>Bytes the download is expected to move.</summary>
    public long TotalBytes { get; }

    /// <summary>
    /// One package, downloaded whole. Velopack passes the download's own progress straight through
    /// here, so the percentage really is the byte fraction.
    /// </summary>
    public static UpdateDownloadPlan Full(long bytes) => bytes > 0 ? new(bytes, [], []) : Unknown;

    /// <summary>
    /// Deltas, in the order Velopack downloads them (by version). Each occupies an equal share of
    /// 0-<see cref="DeltaDownloadCeiling"/> regardless of size, which is why the sizes are kept
    /// individually rather than summed.
    /// </summary>
    public static UpdateDownloadPlan Deltas(IReadOnlyList<long> sizesInDownloadOrder)
    {
        if (sizesInDownloadOrder.Count == 0)
        {
            return Unknown;
        }

        long[] sizes = new long[sizesInDownloadOrder.Count];
        long[] before = new long[sizesInDownloadOrder.Count];
        long running = 0;

        for (int i = 0; i < sizes.Length; i++)
        {
            long size = sizesInDownloadOrder[i];
            if (size < 0 || size > long.MaxValue - running)
            {
                // A negative or overflowing size is not a size. Better no figure than a wrong one.
                return Unknown;
            }

            sizes[i] = size;
            before[i] = running;
            running += size;
        }

        return running > 0 ? new UpdateDownloadPlan(running, sizes, before) : Unknown;
    }

    /// <summary>How many bytes have arrived by the time the updater reports <paramref name="percent"/>.</summary>
    public long BytesAt(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (!IsKnown)
        {
            return 0;
        }

        if (_deltaSizes.Length == 0)
        {
            return (long)(percent / 100.0 * TotalBytes);
        }

        if (percent >= DeltaDownloadCeiling)
        {
            // Everything has arrived; what remains is patching, which moves nothing.
            return TotalBytes;
        }

        double band = (double)DeltaDownloadCeiling / _deltaSizes.Length;
        int index = Math.Min((int)(percent / band), _deltaSizes.Length - 1);
        double within = (percent - (index * band)) / band;

        return _cumulativeBefore[index] + (long)(within * _deltaSizes[index]);
    }
}
