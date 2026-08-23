// <copyright file="UpdateDownloadStatsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;
using CSUploader.Tests.TestSupport;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// The update window's bytes / speed / time-remaining readout, which is derived arithmetic on top of
/// a percentage — Velopack reports an int from 0 to 100 and nothing else.
/// <para>
/// Every test drives a manual clock, so none of them spend the time they describe and none can flake
/// on a slow machine.
/// </para>
/// </summary>
public class UpdateDownloadStatsTests
{
    private const long TenMegabytes = 10 * 1024 * 1024;

    private static (UpdateDownloadStats Stats, ManualTimeProvider Clock) Build(long totalBytes = TenMegabytes)
    {
        ManualTimeProvider clock = new();
        return (new UpdateDownloadStats(UpdateDownloadPlan.Full(totalBytes), clock), clock);
    }

    [Fact]
    public void TheFirstTick_ReportsThePercentAndNothingElse()
    {
        (UpdateDownloadStats stats, _) = Build();

        UpdateDownloadProgress first = stats.Report(0);

        Assert.Equal(0, first.Percent);
        Assert.Equal(0, first.BytesPerSecond);
        Assert.Null(first.Remaining);
    }

    /// <summary>
    /// There is no interval to measure on the first tick, and measuring from construction would
    /// charge the download for however long the caller spent before starting it — which on a slow
    /// update check is seconds, and would report a rate several times too low.
    /// </summary>
    [Fact]
    public void TimeBeforeTheFirstTick_IsNotChargedToTheDownload()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        clock.Advance(TimeSpan.FromSeconds(30)); // the update check, the dialog, the user deciding
        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(10);

        // 10% of 10 MiB in one second.
        Assert.Equal(TenMegabytes / 10, p.BytesPerSecond);
    }

    [Fact]
    public void BytesReceived_TracksThePercentageOfTheTotal()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(25);

        Assert.Equal(TenMegabytes / 4, p.BytesReceived);
        Assert.Equal(TenMegabytes, p.TotalBytes);
        Assert.True(p.HasBytes);
    }

    [Fact]
    public void TheRate_IsMeasuredAcrossThePercentSteps()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        for (int percent = 10; percent <= 50; percent += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            stats.Report(percent);
        }

        // 10% of 10 MiB every 2 s = 5% per second = 512 kB/s, steady, so the mean is exactly that.
        UpdateDownloadProgress p = stats.Report(50);
        Assert.Equal(TenMegabytes / 20, p.BytesPerSecond);
        Assert.True(p.HasRate);
    }

    /// <summary>
    /// Velopack repeats the same percentage between steps. Folding those in as zero-progress
    /// intervals drags the average toward zero for a download that is moving perfectly well — the
    /// readout would sag the longer each percent took, which is backwards.
    /// </summary>
    [Fact]
    public void RepeatedPercentages_DoNotDragTheRateDown()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        long afterOneStep = stats.Report(10).BytesPerSecond;

        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            stats.Report(10);
        }

        Assert.Equal(afterOneStep, stats.Report(10).BytesPerSecond);
    }

    /// <summary>
    /// The other half of the duplicate rule. Not folding duplicates in is only half right — the
    /// timestamp has to survive them too, or the next real change is measured from the last
    /// duplicate instead of from the last actual progress, and every rate comes out too high.
    /// </summary>
    [Fact]
    public void ADuplicateRun_DoesNotShortenTheIntervalThatFollowsIt()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            stats.Report(0); // the same percentage, three seconds of it
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(20);

        // 20% across the whole four seconds, not across the last one.
        Assert.Equal(TenMegabytes / 20, p.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(16), p.Remaining);
    }

    /// <summary>
    /// Why the window sums rather than averaging per-step rates. Progress-spaced samples are not
    /// time-spaced: seven quick percents and one slow one average to something close to the quick
    /// rate, while the window as a whole crawled. The sum is the throughput that actually happened.
    /// </summary>
    [Fact]
    public void OneSlowStepAmongFastOnes_DragsTheRateDownAsItShould()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        int percent = 0;
        for (int i = 0; i < 7; i++)
        {
            percent += 1;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            stats.Report(percent);
        }

        percent += 1;
        clock.Advance(TimeSpan.FromSeconds(10));
        UpdateDownloadProgress p = stats.Report(percent);

        // 8% across 10.7 s is 0.75%/s. A mean of the per-step rates would say about 8.8%/s.
        Assert.InRange(p.BytesPerSecond, TenMegabytes * 0.006, TenMegabytes * 0.009);
    }

    [Fact]
    public void TimeRemaining_ComesFromThePercentageRate()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(20);

        // 20% per second, 80% left → 4 seconds.
        Assert.Equal(TimeSpan.FromSeconds(4), p.Remaining);
    }

    /// <summary>
    /// The advertised size is only ever a guess — the updater prefers delta packages and falls back
    /// to the full one on error, and which it took is invisible from outside. However wrong it is,
    /// the estimate must not move.
    /// <para>
    /// This does NOT distinguish the percentage-based estimate from a byte-based one: the total
    /// cancels out of both, so they agree here. What it pins is the invariance itself, against some
    /// future estimate that does depend on the size. The case that actually requires the
    /// percentage-based form is <see cref="WithNoKnownSize_ThereAreNoBytesButStillATimeRemaining"/>,
    /// where a byte rate would be zero and the estimate would vanish.
    /// </para>
    /// </summary>
    [Fact]
    public void AWrongTotal_DoesNotDistortTimeRemaining()
    {
        ManualTimeProvider clock = new();
        UpdateDownloadStats honest = new(UpdateDownloadPlan.Full(TenMegabytes), clock);
        UpdateDownloadStats wrong = new(UpdateDownloadPlan.Full(TenMegabytes * 37), clock);

        honest.Report(0);
        wrong.Report(0);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(honest.Report(40).Remaining, wrong.Report(40).Remaining);
    }

    [Fact]
    public void AtOneHundredPercent_ThereIsNoTimeRemaining()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(100);

        Assert.Null(p.Remaining);
        Assert.Equal(TenMegabytes, p.BytesReceived);
    }

    /// <summary>
    /// With no size there is nothing to count bytes against, but the clock still works. This is the
    /// test that pins the percentage-based estimate: derive it from the byte rate instead and this
    /// one goes null, because a byte rate against an unknown total is zero.
    /// </summary>
    [Fact]
    public void WithNoKnownSize_ThereAreNoBytesButStillATimeRemaining()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build(totalBytes: 0);

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(25);

        Assert.False(p.HasBytes);
        Assert.Equal(0, p.BytesReceived);
        Assert.False(p.HasRate);
        Assert.Equal(TimeSpan.FromSeconds(3), p.Remaining);
    }

    /// <summary>
    /// Going backwards is what a delta download failing and restarting as the full package looks
    /// like. Averaging across that seam reports a rate that was never true of either side, and the
    /// delta's advertised size no longer describes what is being fetched — so both are dropped.
    /// </summary>
    [Fact]
    public void APercentageThatGoesBackwards_RestartsTheMeasurement()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        clock.Advance(TimeSpan.FromMilliseconds(50)); // the delta: small, so very fast
        stats.Report(90);

        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress afterRestart = stats.Report(5);

        Assert.Equal(5, afterRestart.Percent);
        Assert.Equal(0, afterRestart.BytesPerSecond);
        Assert.Equal(0, afterRestart.TotalBytes);
        Assert.Null(afterRestart.Remaining);

        // ...and it measures the NEW download from here, not from the old baseline: 10% in that
        // second, 85% still to go.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(8.5), stats.Report(15).Remaining);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MinValue, 0)]
    public void APercentageOutsideTheRange_IsClamped(int reported, int expected)
    {
        (UpdateDownloadStats stats, _) = Build();

        Assert.Equal(expected, stats.Report(reported).Percent);
    }

    /// <summary>
    /// Two ticks in the same instant. Dividing by that interval is a division by zero, and the
    /// sample it would produce is meaningless rather than merely large.
    /// </summary>
    [Fact]
    public void TwoTicksAtTheSameInstant_ProduceNoRate()
    {
        (UpdateDownloadStats stats, _) = Build();

        stats.Report(0);
        UpdateDownloadProgress p = stats.Report(50);

        Assert.Equal(0, p.BytesPerSecond);
        Assert.Null(p.Remaining);
        Assert.Equal(TenMegabytes / 2, p.BytesReceived); // the count still holds
    }

    /// <summary>
    /// A rate that changes has to be followed, not averaged over the whole download. The window is
    /// eight samples, so a sustained change replaces it entirely.
    /// </summary>
    [Fact]
    public void ASustainedSlowdown_IsReflectedInTheRate()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build();

        stats.Report(0);
        int percent = 0;
        for (int i = 0; i < 8; i++)
        {
            percent += 5;
            clock.Advance(TimeSpan.FromSeconds(1)); // 5% per second
            stats.Report(percent);
        }

        long fast = stats.Report(percent).BytesPerSecond;

        for (int i = 0; i < 8; i++)
        {
            percent += 1;
            clock.Advance(TimeSpan.FromSeconds(4)); // 0.25% per second — twenty times slower
            stats.Report(percent);
        }

        long slow = stats.Report(percent).BytesPerSecond;
        Assert.True(slow * 10 < fast, $"the rate barely moved: {fast} → {slow}");
    }

    [Fact]
    public void ANegativeTotal_IsTreatedAsUnknownRatherThanTrusted()
    {
        (UpdateDownloadStats stats, ManualTimeProvider clock) = Build(totalBytes: -1);

        stats.Report(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        UpdateDownloadProgress p = stats.Report(50);

        Assert.Equal(0, p.TotalBytes);
        Assert.Equal(0, p.BytesReceived);
    }
}
