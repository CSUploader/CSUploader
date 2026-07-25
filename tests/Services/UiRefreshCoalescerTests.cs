// <copyright file="UiRefreshCoalescerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Services;

namespace CSUploader.Tests.Services;

/// <summary>
/// Pins the coalescing invariant behind the Uploaded-tab freeze: a burst of background completion events must
/// collapse into at most two UI-thread reloads, never one-per-event (a dump showed ~2,000 backed-up dispatcher
/// jobs when it was one-per-event).
/// </summary>
public sealed class UiRefreshCoalescerTests
{
    [Fact]
    public void Request_Burst_CoalescesToOnePost_AndBoundedRuns()
    {
        QueueingDispatcher dispatcher = new();
        int runs = 0;
        UiRefreshCoalescer coalescer = new(dispatcher, () => { runs++; return Task.CompletedTask; });

        for (int i = 0; i < 100; i++)
        {
            coalescer.Request();
        }

        Assert.Equal(1, dispatcher.PostCount); // 100 requests → ONE queued run
        Assert.Equal(0, runs);                 // deferred dispatcher: nothing has run yet

        dispatcher.Drain();

        // 99 requests landed while the run was in flight (state 2) → exactly one follow-up pass. Bounded, not 100.
        Assert.Equal(2, runs);
        Assert.Equal(1, dispatcher.PostCount); // the follow-up loops in-place; no second Post
    }

    [Fact]
    public void Request_ArrivingDuringRun_TriggersExactlyOneFollowup()
    {
        QueueingDispatcher dispatcher = new();
        int runs = 0;
        UiRefreshCoalescer coalescer = null!;
        coalescer = new UiRefreshCoalescer(dispatcher, () =>
        {
            runs++;
            if (runs == 1)
            {
                coalescer.Request(); // a completion lands DURING the first reload
            }

            return Task.CompletedTask;
        });

        coalescer.Request();
        dispatcher.Drain();

        Assert.Equal(2, runs);                 // first run + exactly one follow-up
        Assert.Equal(1, dispatcher.PostCount); // follow-up looped in-place; no second Post
    }

    [Fact]
    public void Request_AfterCompletion_RearmsWithANewPost()
    {
        QueueingDispatcher dispatcher = new();
        int runs = 0;
        UiRefreshCoalescer coalescer = new(dispatcher, () => { runs++; return Task.CompletedTask; });

        coalescer.Request();
        dispatcher.Drain();
        Assert.Equal(1, runs);
        Assert.Equal(1, dispatcher.PostCount);

        coalescer.Request(); // fresh burst after the coalescer went idle
        Assert.Equal(2, dispatcher.PostCount); // re-armed → a new post
        dispatcher.Drain();
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Request_RefreshThrows_DoesNotWedge_NextRequestStillRuns()
    {
        QueueingDispatcher dispatcher = new();
        int runs = 0;
        UiRefreshCoalescer coalescer = new(
            dispatcher,
            () => { runs++; throw new InvalidOperationException("boom"); },
            logger: null);

        coalescer.Request();
        dispatcher.Drain(); // the throw is swallowed; state must return to idle
        Assert.Equal(1, runs);

        coalescer.Request(); // a wedged coalescer would never post again
        dispatcher.Drain();
        Assert.Equal(2, runs);
    }

    // Queues Post callbacks instead of running them inline, so a burst can be issued BEFORE anything runs — the
    // only way to observe coalescing. Single-threaded (the test drives it), so no locking. The coalescer's
    // RunAsync completes synchronously here because the refresh delegate returns an already-completed Task.
    private sealed class QueueingDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int PostCount { get; private set; }

        public void Post(Action action)
        {
            PostCount++;
            _queue.Enqueue(action);
        }

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }

        public Task InvokeAsync(Action action) => throw new NotSupportedException();

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => throw new NotSupportedException();
    }
}
