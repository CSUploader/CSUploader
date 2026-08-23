// <copyright file="ParallelPartUploaderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline;

/// <summary>
/// Running a file's parts with bounded concurrency. Two properties carry the weight: degree 1 must
/// be byte-identical to the sequential loop it replaces, and the failure surfaced to
/// <c>AttemptRunner</c> — which decides retryability from it — must be the causal one, chosen by a
/// rule that does not depend on thread scheduling.
/// </summary>
public class ParallelPartUploaderTests
{
    [Fact]
    public async Task RunAsync_KeepsResultsInPartOrder_HoweverTheyFinish()
    {
        // ETags must line up with part numbers or complete-multipart is rejected, and finishing
        // order is not part order once parts run together.
        PartResult[] results = await ParallelPartUploader.RunAsync(
            partCount: 4,
            degreeOfParallelism: 4,
            uploadPart: async (i, ct) =>
            {
                await Task.Delay((4 - i) * 20, ct); // later parts finish FIRST
                return new PartResult(i + 1, $"etag-{i}", null);
            },
            CancellationToken.None);

        Assert.Equal(["etag-0", "etag-1", "etag-2", "etag-3"], results.Select(r => r.ETag));
    }

    [Fact]
    public async Task RunAsync_NeverExceedsTheRequestedDegree()
    {
        int running = 0;
        int peak = 0;
        Lock sync = new();

        await ParallelPartUploader.RunAsync(12, 3, async (i, ct) =>
        {
            lock (sync)
            {
                peak = Math.Max(peak, ++running);
            }

            await Task.Delay(20, ct);
            lock (sync)
            {
                running--;
            }

            return new PartResult(i + 1, "etag", null);
        }, CancellationToken.None);

        Assert.True(peak <= 3, $"peak concurrency was {peak}, expected at most 3");
    }

    /// <summary>
    /// The safety property of the whole feature: an un-opted-in hoster must behave exactly as it
    /// does today, and today VikingFile returns on the first rejected part rather than uploading
    /// the rest first.
    /// </summary>
    [Fact]
    public async Task RunAsync_AtDegreeOne_StopsAtTheFirstErrorResult()
    {
        List<int> attempted = [];

        PartResult[] results = await ParallelPartUploader.RunAsync(5, 1, (i, ct) =>
        {
            attempted.Add(i);
            return Task.FromResult(i == 1
                ? new PartResult(i + 1, null, "rejected")
                : new PartResult(i + 1, "etag", null));
        }, CancellationToken.None);

        Assert.Equal([0, 1], attempted);
        Assert.Equal("rejected", Array.Find(results, r => r.Error is not null).Error);
    }

    [Fact]
    public async Task RunAsync_AtDegreeOne_IsSequential()
    {
        List<int> order = [];

        await ParallelPartUploader.RunAsync(5, 1, async (i, ct) =>
        {
            await Task.Delay((5 - i) * 5, ct); // later parts would finish first if run together
            order.Add(i);
            return new PartResult(i + 1, "etag", null);
        }, CancellationToken.None);

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public async Task RunAsync_WhenAPartReturnsAnError_StopsStartingNewParts()
    {
        int started = 0;

        await ParallelPartUploader.RunAsync(16, 2, async (i, ct) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(10, ct);
            return i == 1
                ? new PartResult(i + 1, null, "rejected")
                : new PartResult(i + 1, "etag", null);
        }, CancellationToken.None);

        Assert.True(started < 16, $"{started} of 16 parts started after an error");
    }

    [Fact]
    public async Task RunAsync_SurfacesTheRealFault_NotACancellation()
    {
        // AttemptRunner decides retryability from this exception, so it must be the real one.
        await Assert.ThrowsAsync<HttpRequestException>(() => ParallelPartUploader.RunAsync(8, 4,
            async (i, ct) =>
            {
                if (i == 0)
                {
                    await Task.Delay(10, ct);
                    throw new HttpRequestException("the real fault");
                }

                await Task.Delay(5000, ct); // cancelled by the first fault
                return new PartResult(i + 1, "etag", null);
            },
            CancellationToken.None));
    }

    /// <summary>
    /// The causal failure is part 0's HTTP rejection; part 3 dies while draining afterwards. If
    /// thrown faults and error results are tracked separately, part 3's exception wins and the user
    /// is told the wrong thing. Lowest index wins, so the rejection is what surfaces.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenAnErrorResultRacesALaterException_ReportsTheErrorResult()
    {
        // An asynchronous latch, not a Barrier: Task.WhenAll is still lazily enumerating when part 0
        // takes the first semaphore slot, so a blocking SignalAndWait would deadlock before parts
        // 1-3 were ever created.
        int arrived = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        PartResult[] results = await ParallelPartUploader.RunAsync(8, 4, async (i, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == 4)
            {
                allStarted.SetResult();
            }

            await allStarted.Task;

            if (i == 0)
            {
                return new PartResult(1, null, "rejected (HTTP 403)");
            }

            if (i == 3)
            {
                await Task.Delay(20, CancellationToken.None); // deliberately ignores the linked token
                throw new HttpRequestException("collateral damage");
            }

            await Task.Delay(1000, ct);
            return new PartResult(i + 1, "etag", null);
        }, CancellationToken.None);

        Assert.Equal("rejected (HTTP 403)", results[0].Error);
    }

    /// <summary>
    /// Two earlier filters got this wrong in opposite directions, so it gets its own test. Part 3
    /// rejects and cancels the linked token; part 0 independently times out with an OCE carrying
    /// <c>CancellationToken.None</c>. That is a REAL fault, not a consequence of our cancellation —
    /// <c>AttemptRunner</c> treats a None-token OCE as a fault too — so it must be recorded, and
    /// lowest-index-wins means it is what surfaces.
    /// </summary>
    [Fact]
    public async Task RunAsync_RecordsAnUnrelatedCancellation_RatherThanSwallowingIt()
    {
        int arrived = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TaskCanceledException>(() => ParallelPartUploader.RunAsync(8, 4, async (i, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == 4)
            {
                allStarted.SetResult();
            }

            await allStarted.Task;

            if (i == 3)
            {
                return new PartResult(4, null, "rejected"); // cancels the linked token
            }

            if (i == 0)
            {
                // WAIT for the cancellation to actually land before throwing. Without this the test
                // races: part 0 can throw while linked is still uncancelled, and the OLD
                // `when (linked.IsCancellationRequested)` filter would see false, record the
                // exception, and PASS — green-lighting the bug it exists to catch.
                TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (ct.Register(() => cancelled.TrySetResult()))
                {
                    // BOUNDED. An unbounded await turns a regression into a HANG; with a timeout the
                    // same regression is an ordinary failing test.
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                throw new TaskCanceledException("independent timeout", null, CancellationToken.None);
            }

            await Task.Delay(1000, ct);
            return new PartResult(i + 1, "etag", null);
        }, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_AndNoPartFailed_Throws()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallelPartUploader.RunAsync(4, 2,
            (i, ct) => Task.FromResult(new PartResult(i + 1, "etag", null)), cts.Token));
    }
}
