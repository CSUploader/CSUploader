// <copyright file="ParallelPartUploader.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.ExceptionServices;

namespace CSUploader.Upload.Pipeline;

/// <summary>One part's outcome: its 1-based number plus either an ETag or an error. Hosts that do
/// not use ETags (Hostize, DataNodes) leave <see cref="ETag"/> null on success.</summary>
public readonly record struct PartResult(int PartNumber, string? ETag, string? Error);

/// <summary>
/// Runs a file's part uploads with bounded concurrency, returning their results in PART order
/// however they finished.
/// <para>
/// Degree 1 is a real sequential loop — not a degree-1 semaphore — so a hoster that has not opted in
/// keeps byte-identical behaviour, including stopping at the first rejected part rather than
/// uploading the rest first.
/// </para>
/// </summary>
public static class ParallelPartUploader
{
    public static async Task<PartResult[]> RunAsync(
        int partCount,
        int degreeOfParallelism,
        Func<int, CancellationToken, Task<PartResult>> uploadPart,
        CancellationToken cancellationToken)
    {
        PartResult[] results = new PartResult[partCount];

        if (degreeOfParallelism <= 1)
        {
            for (int i = 0; i < partCount; i++)
            {
                // Checked explicitly rather than left to the callback. The parallel branch enforces
                // caller cancellation itself, so without this the effective DEGREE would change the
                // cancellation semantics — a callback that ignores its token, or cancels just before
                // returning successfully, would behave differently at 1 than at 4.
                cancellationToken.ThrowIfCancellationRequested();

                results[i] = await uploadPart(i, cancellationToken).ConfigureAwait(false);
                if (results[i].Error is not null)
                {
                    // An actual failure still wins over cancellation: it is the more useful cause.
                    return results;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim gate = new(degreeOfParallelism, degreeOfParallelism);

        // ONE failure record covering both shapes. Tracking thrown faults separately from error
        // RESULTS lets a part that died while draining mask the HTTP rejection that actually caused
        // the failure — and AttemptRunner decides retryability from whatever it is handed.
        (int Index, ExceptionDispatchInfo? Fault)? primary = null;
        Lock failureSync = new();

        void RecordFailure(int index, ExceptionDispatchInfo? fault)
        {
            lock (failureSync)
            {
                // Lowest part index wins, deterministically. "First to take the lock" is
                // scheduler-dependent, so two runs of the same failure could report different
                // causes; an explicit rule makes the reported error reproducible.
                if (primary is null || index < primary.Value.Index)
                {
                    primary = (index, fault);
                }
            }

            try
            {
                linked.Cancel();
            }
            catch (Exception)
            {
                // A throwing cancellation callback must never replace the recorded failure.
            }
        }

        async Task RunPartAsync(int index)
        {
            try
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // the run is already doomed; this part never started, and never took a slot
            }

            // Checked AFTER the gate as well as through it, because the two are not the same guard:
            // SemaphoreSlim hands a queued waiter its slot at Release time even while cancellation
            // is racing in, and a satisfied waiter cannot be retracted. Under load that let every
            // finishing part admit the next one ahead of the cancel, so parts kept STARTING -
            // spending the user's bandwidth - after a rejection had already doomed the attempt. The
            // failing part records its failure (which cancels) before releasing its own slot, so
            // anything that part admits is guaranteed to see the flag here.
            if (linked.IsCancellationRequested)
            {
                gate.Release();
                return;
            }

            try
            {
                results[index] = await uploadPart(index, linked.Token).ConfigureAwait(false);

                // An error RESULT dooms the attempt just as a thrown fault does — nothing is
                // finalised without every part — so stop spending the user's bandwidth.
                if (results[index].Error is not null)
                {
                    RecordFailure(index, fault: null);
                }
            }
            catch (OperationCanceledException ex) when (linked.IsCancellationRequested && ex.CancellationToken == linked.Token)
            {
                // Swallow ONLY a cancellation that our own linked token actually caused. BOTH
                // conditions are needed. Matching the exception's token distinguishes "we cancelled
                // this" from "this timed out on its own" — an OCE carrying CancellationToken.None is
                // a real fault, and AttemptRunner treats it as one. Also requiring the token to be
                // genuinely cancelled stops a callback that throws `new OperationCanceledException(ct)`
                // of its own accord from being mistaken for our cancellation and silently leaving a
                // default PartResult in the array. Pure caller cancellation is handled by the final
                // ThrowIfCancellationRequested below.
            }
            catch (Exception ex)
            {
                RecordFailure(index, ExceptionDispatchInfo.Capture(ex));
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, partCount).Select(RunPartAsync)).ConfigureAwait(false);

        // A recorded failure always beats the caller's cancellation: if a part genuinely failed,
        // that is the cause worth reporting, and checking cancellation first would hide it behind an
        // OperationCanceledException.
        if (primary is { } failure)
        {
            failure.Fault?.Throw(); // a thrown fault propagates with its original stack
            return results;         // an error RESULT travels back in the array
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }
}
