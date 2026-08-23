// <copyright file="ThrottledStreamConcurrencyTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// What a speed limit MEANS when more than one upload runs at once. The scheduler launches up to
/// <c>AppSettings.MaxConcurrentUploadJobs</c> files concurrently, and each request body wraps its own
/// <see cref="ThrottledStream"/> (HttpHandler constructs one per body). Every instance keeps a PRIVATE
/// 1-second window and byte counter, and the limit delegate returns a RATE rather than drawing on a
/// shared budget — so N concurrent uploads are each allowed the full limit.
/// </summary>
public class ThrottledStreamConcurrencyTests
{
    private const int LimitBytesPerSecond = 100_000;
    private const int PerStreamBytes = 50_000; // 4 x 50k at 100 kB/s is ~2s shared, ~0.5s unshared
    private const int Streams = 4;

    /// <summary>
    /// A user who sets 100 KB/s expects the machine to send 100 KB/s. With four files in flight the
    /// aggregate should still be 100 KB/s, so 200 KB total should take ~2 s. If each stream instead
    /// gets its own 100 KB/s, all four finish their 50 KB in ~0.5 s — a 4x overshoot of the limit.
    /// </summary>
    [Fact]
    public async Task ConcurrentStreams_ShareTheLimit_RatherThanEachGettingItInFull()
    {
        // ONE limiter and ONE budget, shared by every stream — which is the entire fix.
        SpeedLimiter limiter = new(() => LimitBytesPerSecond);
        SpeedBudget budget = new(() => limiter);
        Stopwatch clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, Streams).Select(_ => Task.Run(async () =>
        {
            using ThrottledStream stream = new(new MemoryStream(new byte[PerStreamBytes]), budget);
            byte[] buffer = new byte[8192];
            int total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                total += read;
            }

            Assert.Equal(PerStreamBytes, total);
        })));

        clock.Stop();

        long totalBytes = (long)PerStreamBytes * Streams;
        double seconds = clock.Elapsed.TotalSeconds;
        double aggregateBytesPerSecond = totalBytes / seconds;

        // 25% headroom over the configured limit for the 100ms burst and scheduling jitter.
        Assert.True(
            aggregateBytesPerSecond <= LimitBytesPerSecond * 1.25,
            $"{Streams} concurrent streams moved {totalBytes} bytes in {seconds:F2}s = "
            + $"{aggregateBytesPerSecond / 1000:F0} kB/s aggregate, against a configured limit of "
            + $"{LimitBytesPerSecond / 1000} kB/s.");
    }

    /// <summary>
    /// The single-stream baseline. This is the behaviour the aggregate fix must NOT break: one upload
    /// at the limit still runs at the limit, so a fix that over-corrects (e.g. dividing the budget by a
    /// concurrency count that is wrong when only one file is in flight) fails here.
    /// </summary>
    [Fact]
    public async Task SingleStream_RunsAtTheLimit()
    {
        Stopwatch clock = Stopwatch.StartNew();

        // Hoisted: a fresh limiter per acquisition would never wait and the test would prove nothing.
        SpeedLimiter limiter = new(() => LimitBytesPerSecond);
        using ThrottledStream stream = new(new MemoryStream(new byte[PerStreamBytes]), new SpeedBudget(() => limiter));
        byte[] buffer = new byte[8192];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        clock.Stop();
        double bytesPerSecond = total / clock.Elapsed.TotalSeconds;

        Assert.Equal(PerStreamBytes, total);
        Assert.True(
            bytesPerSecond <= LimitBytesPerSecond * 1.25,
            $"one stream moved {bytesPerSecond / 1000:F0} kB/s against a {LimitBytesPerSecond / 1000} kB/s limit");

        // …and is not throttled to a fraction of it either: the limit is a ceiling, not a target to undershoot.
        Assert.True(
            bytesPerSecond >= LimitBytesPerSecond * 0.5,
            $"one stream managed only {bytesPerSecond / 1000:F0} kB/s of its {LimitBytesPerSecond / 1000} kB/s allowance");
    }

    /// <summary>
    /// An unlimited limit (null) must stay unlimited no matter how many streams run at once — the
    /// shared-budget fix must not accidentally introduce a ceiling where the user asked for none.
    /// </summary>
    [Fact]
    public async Task ConcurrentStreams_WithNoLimit_AreNotThrottled()
    {
        Stopwatch clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, Streams).Select(_ => Task.Run(async () =>
        {
            using ThrottledStream stream = new(new MemoryStream(new byte[PerStreamBytes]), SpeedBudget.Unlimited);
            byte[] buffer = new byte[8192];
            while (await stream.ReadAsync(buffer) > 0)
            {
                // drain
            }
        })));

        clock.Stop();

        // 800 kB of MemoryStream reads with no limit is milliseconds' work; a full second means a
        // ceiling crept in where the user asked for none.
        Assert.True(
            clock.Elapsed.TotalSeconds < 1,
            $"unlimited streams took {clock.Elapsed.TotalSeconds:F2}s — something is throttling them");
    }
}
