// <copyright file="SpeedBudgetTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Which bucket a stream draws on, and what happens when that changes underneath it. The user can
/// set or clear a file or package speed limit while that file is uploading, so the owning scope is
/// re-resolved every iteration AND re-confirmed after every grant.
/// </summary>
public class SpeedBudgetTests
{
    [Fact]
    public async Task Acquire_DiscardsAGrantFromAScopeThatStoppedBeingTheOwner()
    {
        // The resolve-then-try gap: `resolve` hands out the override bucket once, then switches to
        // the package bucket, as clearing the override would. The grant from the dead bucket must
        // not be returned.
        ManualTimeProvider clock = new();
        long? overrideLimit = 1_000;
        SpeedLimiter overrideBucket = new(() => overrideLimit, clock);
        SpeedLimiter packageBucket = new(() => 100_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        int resolveCount = 0;
        SpeedBudget budget = new(() =>
        {
            if (resolveCount++ == 0)
            {
                return overrideBucket;
            }

            overrideLimit = null;
            return packageBucket;
        });

        SpeedReservation r = await budget.AcquireAsync(8192, CancellationToken.None);

        Assert.Same(packageBucket, r.Limiter);
        Assert.NotSame(overrideBucket, r.Limiter);
    }

    /// <summary>
    /// The ABA hole. The SAME limiter is resolved before and after — so reference identity alone
    /// passes — but in between its provider read null and it handed back the whole request having
    /// charged nothing. That grant bypasses the bucket entirely and must be rejected: an uncharged
    /// grant is only trustworthy from the STATIC unlimited limiter, which can never become limited.
    /// </summary>
    [Fact]
    public async Task Acquire_RejectsAnUnchargedGrant_FromALimiterThatIsNotTheStaticUnlimited()
    {
        ManualTimeProvider clock = new();
        long? overrideLimit = 10_000; // A starts LIMITED, so the transient clear is a real change
        SpeedLimiter a = new(() => overrideLimit, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // capacity = 1_000

        int calls = 0;
        SpeedBudget budget = new(() =>
        {
            // Call 1 clears the override, so A grants everything uncharged. Every later call
            // restores it, so the confirming resolve returns A again and identity matches.
            overrideLimit = ++calls == 1 ? null : 10_000;
            return a;
        });

        SpeedReservation r = await budget.AcquireAsync(81_920, CancellationToken.None);

        Assert.NotNull(r.Limiter); // it had to be CHARGED to be returned
        Assert.True(r.Bytes <= 1_000, $"granted {r.Bytes} bytes against a 1,000-byte capacity");
    }

    [Fact]
    public async Task Acquire_AcceptsAnUnchargedGrant_FromTheStaticUnlimited()
    {
        // The legitimate uncharged case must still short-circuit: the static instance can never
        // become limited, so there is nothing to bypass.
        SpeedReservation r = await SpeedBudget.Unlimited.AcquireAsync(81_920, CancellationToken.None);

        Assert.Null(r.Limiter);
        Assert.Equal(81_920, r.Bytes);
    }

    [Fact]
    public async Task Acquire_WhenOwnershipFlapsEveryIteration_YieldsRatherThanSpinning()
    {
        // Refunding and retrying with no delay burns a core until cancellation when a resolver
        // alternates. Bounded CPU is the assertion; cancellation works either way.
        ManualTimeProvider clock = new();
        SpeedLimiter a = new(() => 1_000_000, clock);
        SpeedLimiter b = new(() => 1_000_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        int i = 0;
        SpeedBudget budget = new(() => i++ % 2 == 0 ? a : b);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.AcquireAsync(1024, cts.Token).AsTask());

        Assert.True(i < 500, $"resolved {i} times in 200ms — the loop is spinning, not yielding");
    }

    [Fact]
    public async Task Acquire_ReturnsAReservationBoundToTheGrantingLimiter()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter bucket = new(() => 10_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        SpeedBudget budget = new(() => bucket);

        SpeedReservation r = await budget.AcquireAsync(500, CancellationToken.None);

        Assert.Same(bucket, r.Limiter);
        Assert.Equal(500, r.Bytes);
    }

    [Fact]
    public async Task Unlimited_NeverThrottlesAndReservesNothing()
    {
        SpeedReservation r = await SpeedBudget.Unlimited.AcquireAsync(8192, CancellationToken.None);

        Assert.Equal(8192, r.Bytes);
        Assert.Null(r.Limiter);
    }

    /// <summary>Guards a hang, not a rate: the loop exits on Bytes > 0, which a zero request can
    /// never satisfy. Stream.ReadAsync(Memory&lt;byte&gt;.Empty) is legal, so this is reachable.</summary>
    [Fact]
    public async Task Acquire_OfZeroBytes_ReturnsImmediately()
    {
        SpeedBudget budget = new(() => new SpeedLimiter(() => 10_000, new ManualTimeProvider()));

        Task<SpeedReservation> acquire = budget.AcquireAsync(0, CancellationToken.None).AsTask();

        Assert.Same(acquire, await Task.WhenAny(acquire, Task.Delay(1000)));
        Assert.Equal(0, (await acquire).Bytes);
    }

    [Fact]
    public async Task Acquire_HonoursCancellationWhileWaiting()
    {
        // The manual clock never advances, so the bucket never fills and the loop only exits on
        // cancellation.
        SpeedLimiter bucket = new(() => 1_000, new ManualTimeProvider());
        SpeedBudget budget = new(() => bucket);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.AcquireAsync(8192, cts.Token).AsTask());
    }
}
