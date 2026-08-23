// <copyright file="SpeedLimiterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// The token bucket behind a speed limit. Every case runs on a <see cref="ManualTimeProvider"/>, so
/// these are exact rather than timing-dependent — the wall-clock coupling of real streams is
/// <c>ThrottledStreamConcurrencyTests</c>'s job.
/// </summary>
public class SpeedLimiterTests
{
    private static (SpeedLimiter Limiter, ManualTimeProvider Clock) Build(Func<long?> rate)
    {
        ManualTimeProvider clock = new();
        return (new SpeedLimiter(rate, clock), clock);
    }

    /// <summary>Takes everything the bucket can currently give. Terminates because the manual clock
    /// does not advance while it runs.</summary>
    private static long Drain(SpeedLimiter limiter)
    {
        long total = 0;
        while (true)
        {
            SpeedReservation r = limiter.TryAcquire(int.MaxValue);
            if (r.Bytes == 0)
            {
                return total;
            }

            total += r.Bytes;
        }
    }

    [Fact]
    public void TheBucketStartsEmpty()
    {
        (SpeedLimiter limiter, _) = Build(() => 100_000);

        Assert.Equal(0, Drain(limiter));
    }

    [Fact]
    public void TokensAccrueAtTheConfiguredRate()
    {
        (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
        clock.Advance(TimeSpan.FromMilliseconds(50)); // below capacity, so nothing is clipped

        Assert.Equal(5_000, Drain(limiter));
    }

    /// <summary>
    /// The burst bound, which is a stated product decision: a bucket banks while idle, and the
    /// global one lives as long as AppSettings, so an unbounded capacity would let a paused queue
    /// release a full second of data the instant it resumed.
    /// </summary>
    [Fact]
    public void AnIdleBucket_BanksAtMostOneTenthOfASecond()
    {
        (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(10_000, Drain(limiter)); // capacity, not 1_000_000
    }

    [Fact]
    public void ConcurrentCallers_ShareOneBucket_RatherThanEachGettingTheLimit()
    {
        (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // one capacity's worth

        long granted = 0;
        for (int caller = 0; caller < 4; caller++)
        {
            granted += Drain(limiter);
        }

        Assert.Equal(10_000, granted); // NOT 40_000
    }

    /// <summary>
    /// Accruing the whole elapsed interval at the previously-seen rate over-grants when the rate is
    /// LOWERED: primed at 1 MB/s, dropped to 10 kB/s, 10 ms elapsed, and a naive implementation
    /// hands over a full second of the new limit. 100 bytes were earned.
    /// </summary>
    [Fact]
    public void LoweringTheRate_DoesNotRetroactivelyEarnAtTheOldRate()
    {
        long? rate = 1_000_000;
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => rate, clock);

        rate = 10_000;
        clock.Advance(TimeSpan.FromMilliseconds(10));

        Assert.Equal(100, Drain(limiter));
    }

    /// <summary>The deliberate conservative side of sampling: the interval spanning a change is
    /// accounted at the LOWER rate. Documented, not accidental.</summary>
    [Fact]
    public void RaisingTheRate_UnderGrantsForTheSampledInterval_RatherThanOverGranting()
    {
        long? rate = 10_000;
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => rate, clock);

        rate = 1_000_000;
        clock.Advance(TimeSpan.FromMilliseconds(10));

        Assert.Equal(100, Drain(limiter)); // 10 ms at the OLD rate, not the new one
    }

    [Fact]
    public void Unlimited_GrantsEverythingAndReservesNothing()
    {
        SpeedReservation r = SpeedLimiter.Unlimited.TryAcquire(8192);

        Assert.Equal(8192, r.Bytes);
        Assert.Null(r.Limiter); // nothing to refund to; the zero-cost path
    }

    [Fact]
    public void Refund_ReturnsTokensToTheBucketThatGrantedThem()
    {
        (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 10_000);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // capacity = 1_000

        SpeedReservation first = limiter.TryAcquire(1_000);
        Assert.Equal(1_000, first.Bytes);
        Assert.Equal(0, limiter.TryAcquire(1).Bytes); // exhausted

        first.Refund(1_000);

        Assert.Equal(500, limiter.TryAcquire(500).Bytes);
    }

    [Fact]
    public void Refund_CannotInflateTheBucketAboveCapacity()
    {
        (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 10_000);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        SpeedReservation r = limiter.TryAcquire(1_000);
        clock.Advance(TimeSpan.FromSeconds(5)); // refills to capacity on its own
        r.Refund(1_000);

        Assert.Equal(1_000, Drain(limiter)); // capacity, not 2_000
    }

    /// <summary>An empty bucket would stall the first read for 0.82s at 100 kB/s if the wait
    /// targeted a full 80 kB buffer. Targeting one capacity keeps the stream flowing.</summary>
    [Fact]
    public void EstimateWait_TargetsACapacityFill_NotTheWholeRequest()
    {
        (SpeedLimiter limiter, _) = Build(() => 100_000);

        TimeSpan wait = limiter.EstimateWait(81_920);

        Assert.InRange(wait.TotalMilliseconds, 50, 150);
    }
}
