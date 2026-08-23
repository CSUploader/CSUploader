// <copyright file="PartProgressAggregator.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Turns per-part byte counts into one file-level total while parts are in flight together.
/// <para>
/// The sequential path reports <c>basePosition + bytesInThisChunk</c>, an absolute file position
/// that is only meaningful when parts complete in order. Run them together and that value lurches
/// backwards whenever a lower-numbered part reports after a higher one — and
/// <c>OperationProgressEventArgs</c> derives bytes-remaining, percentage, speed, ETA and finish time
/// from it, so the damage is not merely cosmetic.
/// </para>
/// <para>
/// Each part keeps a HIGH-WATER MARK rather than its latest value. UploadNow retries a part
/// internally, and the retry restarts that part's counter from near zero; subtracting the old
/// contribution would make the file's total fall, which is the very thing this class prevents. With
/// a high-water mark the total simply plateaus while the resent part catches up.
/// </para>
/// <para>
/// Totals are computed AND queued under the lock, then drained by a single caller outside it.
/// Publishing under the lock would run arbitrary subscriber code — ultimately the UI — while holding
/// it, which invites contention and deadlock and lets a subscriber's exception escape into
/// request-body serialization, turning a progress failure into an upload failure.
/// </para>
/// </summary>
public sealed class PartProgressAggregator
{
    private readonly long[] _highWaterPerPart;
    private readonly Action<long> _publish;
    private readonly Lock _sync = new();
    private readonly Queue<long> _pending = new();
    private long _total;
    private bool _draining;

    public PartProgressAggregator(int partCount, Action<long> publish)
    {
        // Validated rather than left to fail later. A null publisher throws inside Drain, which
        // deliberately swallows subscriber exceptions — so progress would vanish silently and
        // forever. A zero part count fails only on the first report, far from the mistake.
        ArgumentOutOfRangeException.ThrowIfLessThan(partCount, 1);
        ArgumentNullException.ThrowIfNull(publish);

        _highWaterPerPart = new long[partCount];
        _publish = publish;
    }

    /// <summary>
    /// Records <paramref name="cumulativeBytesInThatPart"/> as part <paramref name="partIndex"/>'s
    /// running total and publishes the file-wide sum. A value at or below what that part has already
    /// reported is a retry replaying ground already counted, and is ignored.
    /// </summary>
    public void Report(int partIndex, long cumulativeBytesInThatPart)
    {
        lock (_sync)
        {
            long previous = _highWaterPerPart[partIndex];
            if (cumulativeBytesInThatPart <= previous)
            {
                return;
            }

            _total += cumulativeBytesInThatPart - previous;
            _highWaterPerPart[partIndex] = cumulativeBytesInThatPart;
            _pending.Enqueue(_total);

            if (_draining)
            {
                // Another caller owns the drain and will pick this up; the queue preserves order.
                return;
            }

            _draining = true;
        }

        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            long next;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }

                next = _pending.Dequeue();
            }

            try
            {
                _publish(next);
            }
            catch (Exception)
            {
                // A progress subscriber must never fail an upload. Swallowing also keeps the queue
                // moving: an escaping exception would leave _draining latched true and silently
                // stop every later report.
            }
        }
    }
}
