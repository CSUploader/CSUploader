// <copyright file="DeferredUiDispatcher.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// The <see cref="InlineUiDispatcher"/>'s sibling for testing dispatch-gap races: <c>Post</c>
/// CAPTURES the action instead of running it, and the test runs the backlog explicitly with
/// <see cref="RunPosted"/>. That turns "between the event firing and the UI callback getting its
/// turn" — a window a real dispatcher opens under load — into a place a test can stand: capture the
/// posted callback, change the world, then let it run and assert it noticed.
/// </summary>
/// <remarks>
/// <c>InvokeAsync</c> stays inline and timers stay manually tickable, exactly like
/// <see cref="InlineUiDispatcher"/> — only <c>Post</c> defers, because Post-routed handlers are
/// where the stale-event races live. Thread-safe: production code posts from the scheduler pump and
/// the persistence chain, not the test thread.
/// </remarks>
public sealed class DeferredUiDispatcher : CSUploader.Services.IUiDispatcher
{
    private readonly List<Action> _posted = [];
    private readonly Lock _lock = new();

    public List<InlineUiDispatcher.TestTimer> Timers { get; } = [];

    /// <summary>Gets the number of captured, not-yet-run actions.</summary>
    public int PostedCount
    {
        get
        {
            lock (_lock)
            {
                return _posted.Count;
            }
        }
    }

    public void Post(Action action)
    {
        lock (_lock)
        {
            _posted.Add(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public CSUploader.Services.IUiTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        InlineUiDispatcher.TestTimer timer = new(onTick);
        Timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Runs every captured action in the order it was posted, including any that running them
    /// posts in turn — the way a real dispatcher would drain its queue.
    /// </summary>
    public void RunPosted()
    {
        while (true)
        {
            Action[] batch;
            lock (_lock)
            {
                if (_posted.Count == 0)
                {
                    return;
                }

                batch = [.. _posted];
                _posted.Clear();
            }

            foreach (Action action in batch)
            {
                action();
            }
        }
    }
}
