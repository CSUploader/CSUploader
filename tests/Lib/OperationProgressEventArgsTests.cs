// <copyright file="OperationProgressEventArgsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Tests.Lib;

/// <summary>
/// The derived estimates on <see cref="OperationProgressEventArgs"/>. The clock is the real
/// <c>DateTime.Now</c> — the class offers no injection — so every assertion here is a RELATIONSHIP
/// between the object's own values and timestamps taken around construction, never an exact time.
/// </summary>
public class OperationProgressEventArgsTests
{
    private const long OneMiB = 1024L * 1024;

    /// <summary>
    /// The invariant that used to be broken: the finish estimate is "now plus the remaining time".
    /// </summary>
    /// <remarks>
    /// It was computed from <c>TimeRemaining</c> BEFORE that value was calculated — and since
    /// production constructs a fresh instance per progress event, "before" meant the field's zero
    /// default, making <c>DateTimeFinish</c> plain now on every event.
    /// <para>
    /// The scenario makes the two outcomes unconfusable: 10 seconds elapsed for 1 MiB of 1 GiB puts
    /// TimeRemaining in the HOURS (asserted, so the check cannot pass vacuously), and the two-sided
    /// window around construction is seconds wide. The broken ordering lands hours below the window;
    /// no clock jitter can bridge that.
    /// </para>
    /// </remarks>
    [Fact]
    public void DateTimeFinish_IsNowPlusTimeRemaining()
    {
        DateTime before = DateTime.Now;
        OperationProgressEventArgs args = new(
            size: 1024 * OneMiB,
            bytesProcessed: OneMiB,
            dateTimeStarted: before.AddSeconds(-10));
        DateTime after = DateTime.Now;

        Assert.True(args.TimeRemaining > TimeSpan.FromHours(1), $"TimeRemaining was {args.TimeRemaining}; the scenario should put it in the hours.");

        DateTime impliedNow = args.DateTimeFinish - args.TimeRemaining;
        Assert.InRange(impliedNow, before.AddSeconds(-1), after.AddSeconds(1));
    }

    /// <summary>
    /// The estimate itself: remaining time extrapolates the observed rate over the remaining bytes.
    /// 10 seconds for 1 MiB of 1024 MiB extrapolates to 1023 more of the same — ~2.8 hours.
    /// </summary>
    [Fact]
    public void TimeRemaining_ExtrapolatesTheObservedRate()
    {
        OperationProgressEventArgs args = new(
            size: 1024 * OneMiB,
            bytesProcessed: OneMiB,
            dateTimeStarted: DateTime.Now.AddSeconds(-10));

        // Elapsed is measured off the real clock, so it is "10 seconds plus however long the test
        // took to get here" — bounded loosely, but hours-tight against the zero the old code fed in.
        Assert.InRange(args.TimeRemaining, TimeSpan.FromHours(2.5), TimeSpan.FromHours(3.5));
        Assert.Equal(1023 * OneMiB, args.BytesRemaining);
    }

    [Fact]
    public void NothingProcessed_DerivesNothing()
    {
        OperationProgressEventArgs args = new(
            size: 1024 * OneMiB,
            bytesProcessed: 0,
            dateTimeStarted: DateTime.Now.AddSeconds(-10));

        // The guard leaves every derived value at its default rather than dividing by zero.
        Assert.Equal(TimeSpan.Zero, args.TimeRemaining);
        Assert.Equal(default, args.DateTimeFinish);
        Assert.Equal(0, args.Speed);
    }
}
