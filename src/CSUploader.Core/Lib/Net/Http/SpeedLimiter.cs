// <copyright file="SpeedLimiter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// A byte budget shared by every stream governed by the same speed limit, as a continuous token
/// bucket whose capacity is deliberately small.
/// <para>
/// A limit must be enforced ACROSS concurrent transfers, not within each one. The scheduler runs up
/// to <c>MaxConcurrentUploadJobs</c> files at once (default 5) and every request body used to wrap
/// its own throttle with its own private counter, so N uploads each got the full limit — a user's
/// 1 MB/s became 5 MB/s. Measured before the fix: four streams at a configured 100 kB/s moved
/// 800,000 bytes in 2.03 s, an aggregate of 394 kB/s.
/// </para>
/// <para>
/// Capacity is <b>one tenth of a second</b> at the current rate. Any bucket banks tokens while idle,
/// and the global one lives as long as <c>AppSettings</c> — so an unbounded capacity would let a
/// paused queue release a full second of data the instant it resumed. A tenth of a second is small
/// enough to be imperceptible and large enough to avoid a wakeup every few bytes. Precisely: over an
/// interval T at a constant rate R, delivered bytes are bounded by <c>R*T + 0.1R</c> — the excess is
/// 100 ms worth of data ONCE, which is +10% at T=1s and +1% at T=10s.
/// </para>
/// <para>
/// Guarantees the AGGREGATE rate. Does not promise per-stream fairness (the split between concurrent
/// streams is unmanaged), a prompt wakeup when a sibling refunds, or exact accrual across a rate
/// change — rates are sampled per read, not observed as events.
/// </para>
/// </summary>
public sealed class SpeedLimiter
{
    /// <summary>Capacity, and the wait target, are one Nth of a second. See the class remarks.</summary>
    private const int BurstFraction = 10;

    private readonly Func<long?> _getBytesPerSecond;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private double _tokens;
    private long _lastTimestamp;
    private long _lastRate;

    public SpeedLimiter(Func<long?> getBytesPerSecond, TimeProvider? time = null)
    {
        _getBytesPerSecond = getBytesPerSecond;
        _time = time ?? TimeProvider.System;

        // Stamp at construction, not on first use. Starting the clock lazily makes every caller that
        // lets time pass before its first read see a zero interval — which under-throttles a delayed
        // first read and silently breaks any test that advances a manual clock before touching this.
        _lastTimestamp = _time.GetTimestamp();
        _lastRate = getBytesPerSecond() is long rate and > 0 ? rate : 0;
        _tokens = 0; // starts empty
    }

    /// <summary>A limiter that never throttles, and never touches shared state doing it.</summary>
    public static SpeedLimiter Unlimited { get; } = new(() => null);

    /// <summary>The limit currently in force, or null when unlimited. Re-read live.</summary>
    public long? CurrentLimitBytesPerSecond => _getBytesPerSecond();

    /// <summary>
    /// Takes what the bucket can afford right now, up to <paramref name="requestedBytes"/> —
    /// possibly ZERO. Never waits: waiting, and re-resolving which scope owns the stream, belong to
    /// <see cref="SpeedBudget"/>, so a limit changed mid-wait is actually noticed.
    /// </summary>
    internal SpeedReservation TryAcquire(int requestedBytes)
    {
        if (requestedBytes <= 0)
        {
            return SpeedReservation.None;
        }

        // Read once before the lock, only to take the zero-cost unlimited exit. The authoritative
        // read happens INSIDE the lock: a caller that sampled a stale high rate must not refill or
        // grant at it after someone else lowered the limit.
        if (_getBytesPerSecond() is null or <= 0)
        {
            return new SpeedReservation(null, requestedBytes);
        }

        lock (_sync)
        {
            long? bps = _getBytesPerSecond();
            if (bps is null or <= 0)
            {
                return new SpeedReservation(null, requestedBytes);
            }

            Refill(bps.Value);
            int granted = (int)Math.Min(requestedBytes, (long)_tokens);
            if (granted <= 0)
            {
                return SpeedReservation.None;
            }

            _tokens -= granted;
            return new SpeedReservation(this, granted);
        }
    }

    /// <summary>
    /// How long until one capacity has accrued — not until the caller's whole request can be met,
    /// which at a low limit would stall the first read for most of a second.
    /// </summary>
    internal TimeSpan EstimateWait(int requestedBytes)
    {
        lock (_sync)
        {
            long? bps = _getBytesPerSecond();
            if (bps is null or <= 0)
            {
                return TimeSpan.Zero;
            }

            Refill(bps.Value);
            double target = Math.Min(requestedBytes, CapacityFor(bps.Value));
            double shortfall = Math.Max(0, target - _tokens);
            return TimeSpan.FromSeconds(shortfall / bps.Value);
        }
    }

    /// <summary>
    /// Returns unused bytes from a grant. A short read, an EOF probe, or a cancelled read must not
    /// spend budget it never moved — <c>ProgressStreamContent</c> reads in 81,920-byte buffers until
    /// zero, so one EOF probe could otherwise consume an entire low-rate bucket.
    /// </summary>
    internal void Refund(int unusedBytes)
    {
        lock (_sync)
        {
            long? bps = _getBytesPerSecond();

            // Clamp against the current capacity, or the last known one while unlimited — a refund
            // of bytes that were never moved must not be silently discarded just because the limit
            // happens to be off at this instant.
            double capacity = CapacityFor(bps is > 0 ? bps.Value : _lastRate);
            _tokens = Math.Min(capacity, _tokens + unusedBytes);
        }
    }

    private static double CapacityFor(long bytesPerSecond) => Math.Max(1, bytesPerSecond / (double)BurstFraction);

    private void Refill(long bps)
    {
        long now = _time.GetTimestamp();
        double elapsedSeconds = (double)(now - _lastTimestamp) / _time.TimestampFrequency;
        _lastTimestamp = now;

        // Accrue at the LOWER of the rate we last saw and the rate now in force. Rates are sampled
        // per read, not observed as events, so the interval containing a change cannot be split
        // exactly. The minimum is the conservative choice ACROSS ADJACENT SAMPLES: it under-grants
        // briefly after a raise and never over-grants after a drop — where accruing at _lastRate
        // alone handed out a full second of a newly-lowered limit after 10 ms.
        //
        // It is not conservative against a change that REVERTS between two samples: drop to 1 kB/s
        // for a second and back to 100 kB/s before the next read, and both endpoints read 100 kB/s.
        // Sampling cannot see that, and the over-accrual is bounded by the capacity — the documented
        // 100 ms burst — so it is accepted rather than designed around.
        long accrualRate = _lastRate > 0 ? Math.Min(_lastRate, bps) : bps;
        _tokens += elapsedSeconds * accrualRate;

        // Capacity always follows the CURRENT rate, so lowering the limit forfeits an oversized bank.
        _tokens = Math.Min(CapacityFor(bps), _tokens);
        _lastRate = bps;
    }
}
