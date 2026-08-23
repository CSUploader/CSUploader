// <copyright file="MegaWebSocketUploaderThrottleTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// MEGA and TransferIt obey the speed limit.
/// <para>
/// This path is the one exception to "throttling happens in <c>ThrottledStream</c>": it opens the
/// file itself and writes ciphertext straight to a <c>ClientWebSocket</c>, never touching
/// <c>HttpHandler</c>. So it charges the shared budget directly, and these tests cover that charge
/// rather than the socket — a real WebSocket is not what is in doubt.
/// </para>
/// </summary>
public class MegaWebSocketUploaderThrottleTests
{
    private static long Remaining(SpeedLimiter limiter)
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
    public async Task ChargeAsync_TakesExactlyTheBytesAboutToBeSent()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 10_000_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // capacity = 1,000,000

        await MegaWebSocketUploader.ChargeAsync(new SpeedBudget(() => limiter), 4096, CancellationToken.None);

        Assert.Equal(1_000_000 - 4096, Remaining(limiter));
    }

    [Fact]
    public async Task ChargeAsync_KeepsAcquiringUntilTheWholeChunkIsCovered()
    {
        // A grant is capped by what the bucket can currently afford, so one acquisition is not
        // enough for a chunk larger than the capacity. Charging only the first grant would let the
        // rest of the chunk go out unpaid for.
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 1_000_000, clock); // capacity = 100,000
        clock.Advance(TimeSpan.FromMilliseconds(100));      // exactly one capacity available

        Task charge = MegaWebSocketUploader.ChargeAsync(
            new SpeedBudget(() => limiter), 250_000, CancellationToken.None);

        // One bucketful cannot cover 250,000. An implementation that charged only the first grant
        // and sent anyway would have completed by now.
        await Task.Delay(150);
        Assert.False(
            charge.IsCompleted,
            "the charge completed after a single grant — the rest of the chunk would go out unpaid for");

        // Let the bucket refill until the whole chunk is covered.
        for (int i = 0; i < 30 && !charge.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(30);
        }

        Assert.True(charge.IsCompleted, "the charge never completed");
        await charge;
    }

    [Fact]
    public async Task ChargeAsync_WithNoBudget_ReturnsImmediately()
    {
        // The null case is the pipelines' "no limit configured" path and must cost nothing.
        await MegaWebSocketUploader.ChargeAsync(null, 4096, CancellationToken.None);
    }

    [Fact]
    public async Task ChargeAsync_WithAnUnlimitedBudget_ReturnsImmediately()
    {
        ManualTimeProvider clock = new();
        Task charge = MegaWebSocketUploader.ChargeAsync(SpeedBudget.Unlimited, 8 * 1024 * 1024, CancellationToken.None);

        Assert.Same(charge, await Task.WhenAny(charge, Task.Delay(1000)));
        await charge;
    }

    [Fact]
    public async Task ChargeAsync_HonoursCancellation()
    {
        // The send loop's token must be able to abandon a charge, or a cancelled upload would sit
        // waiting on a bucket it no longer needs.
        SpeedLimiter limiter = new(() => 1_000, new ManualTimeProvider()); // frozen clock: never fills
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MegaWebSocketUploader.ChargeAsync(new SpeedBudget(() => limiter), 8192, cts.Token));
    }
}
