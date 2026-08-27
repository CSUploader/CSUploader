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

    /// <summary>
    /// Once a part has failed, no waiting part ever starts — exactly, not "fewer than all of
    /// them". The assert is deterministic because the guarantee is causal, not timed: the failing
    /// part records its failure (which cancels) BEFORE its <c>finally</c> releases its slot, so by
    /// the time any slot frees, every queued waiter either has its <c>WaitAsync</c> cancelled
    /// outright or is admitted into the post-gate cancellation check — and both routes end without
    /// invoking the delegate. Parts 0 and 1 hold both slots on completion sources; the test
    /// releases NOTHING until the failure's cancellation is observable (via the linked token the
    /// delegate was handed), because completing part 0 while part 1's rejection is still in flight
    /// re-creates the exact race under test: part 0's release admits part 2 ahead of the cancel —
    /// a first draft of this test did exactly that and flaked, as did the 10ms-delay version
    /// before it, which could pass with any number of unwanted starts.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenAPartReturnsAnError_StartsNoFurtherParts()
    {
        var started = new System.Collections.Concurrent.ConcurrentQueue<int>();
        TaskCompletionSource<PartResult>[] holds =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        int arrived = 0;
        TaskCompletionSource bothSlotsHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource runCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<PartResult[]> run = ParallelPartUploader.RunAsync(16, 2, async (i, ct) =>
        {
            started.Enqueue(i);
            if (i == 0)
            {
                // The linked token, straight from the SUT: RecordFailure cancels it before the
                // failing part's slot is released, so its firing is the happens-before edge the
                // test must see before letting part 0 finish.
                ct.Register(() => runCancelled.TrySetResult());
            }

            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothSlotsHeld.SetResult();
            }

            // Any part past the first two is already a regression (asserted below); completing it
            // keeps the failure an assert instead of a hang.
            return i < 2 ? await holds[i].Task : new PartResult(i + 1, "etag", null);
        }, CancellationToken.None);

        await bothSlotsHeld.Task;
        holds[1].SetResult(new PartResult(2, null, "rejected")); // the failure; part 0 still holds the other slot

        // Bounded so a SUT that stops cancelling fails inside the timeout instead of hanging the run.
        Assert.Same(runCancelled.Task, await Task.WhenAny(runCancelled.Task, Task.Delay(TimeSpan.FromSeconds(10))));

        holds[0].SetResult(new PartResult(1, "etag-0", null));
        PartResult[] results = await run;

        Assert.Equal([0, 1], started.Order());
        Assert.Equal("rejected", results[1].Error);
        Assert.Equal("etag-0", results[0].ETag);
        Assert.All(results.Skip(2), r => Assert.Equal(default, r)); // never started, never written
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
    public async Task RunAsync_WhenAPartThrowsAnUncancelledOce_TreatsItAsAFault()
    {
        // A callback throwing `new OperationCanceledException(ct)` WITHOUT the token being cancelled
        // is not our cancellation — it is that part failing. Matching on token identity alone would
        // swallow it and leave a default PartResult in the array, so the upload would "succeed"
        // with a missing ETag.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallelPartUploader.RunAsync(4, 2,
            (i, ct) => i == 0
                ? throw new OperationCanceledException(ct)
                : Task.FromResult(new PartResult(i + 1, "etag", null)),
            CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_AtDegreeOne_HonoursCallerCancellation_LikeTheParallelPath()
    {
        // Changing the effective degree must not change cancellation semantics. This callback
        // deliberately ignores its token, exactly as a sloppy pipeline might.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallelPartUploader.RunAsync(4, 1,
            (i, ct) => Task.FromResult(new PartResult(i + 1, "etag", null)), cts.Token));
    }

    [Fact]
    public async Task RunAsync_AtDegreeOne_WhenAPartFailsAndTheCallerCancels_ReportsTheFailure()
    {
        // An actual failure is the more useful cause, so it wins over cancellation here too.
        using CancellationTokenSource cts = new();

        PartResult[] results = await ParallelPartUploader.RunAsync(4, 1, (i, ct) =>
        {
            if (i == 0)
            {
                cts.Cancel();
                return Task.FromResult(new PartResult(1, null, "rejected"));
            }

            return Task.FromResult(new PartResult(i + 1, "etag", null));
        }, cts.Token);

        Assert.Equal("rejected", results[0].Error);
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
