// <copyright file="AvaloniaUiDispatcherTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Services;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Pins the <c>IUiDispatcher</c> asymmetric-exception contract (IUiDispatcher.cs:16-31) for the
/// Avalonia implementation: <c>Post</c> queues (never inline); <c>InvokeAsync</c> throws to the awaiter
/// on the inline path but, on the marshaled path, routes the exception to the framework's
/// unhandled-exception path (observed here through the <c>MarshaledExceptionSink</c> test seam) while
/// the returned Task completes without faulting; <c>CreateTimer</c> hands back a stopped timer.
/// </summary>
public class AvaloniaUiDispatcherTests
{
    [AvaloniaFact]
    public void Post_OnUiThread_QueuesInsteadOfRunningInline()
    {
        AvaloniaUiDispatcher dispatcher = new();
        bool ran = false;

        dispatcher.Post(() => ran = true);

        Assert.False(ran); // queued-never-inline (contract)
        Dispatcher.UIThread.RunJobs();
        Assert.True(ran);
    }

    [AvaloniaFact]
    public void InvokeAsync_OnUiThread_ThrowsInlineToCaller()
    {
        AvaloniaUiDispatcher dispatcher = new();
        InvalidOperationException boom = new("inline boom");

        // On the UI thread CheckAccess() is true, so the action runs inline and its exception surfaces
        // synchronously to the caller — it is NOT captured in the returned Task (contract). xUnit2014
        // assumes any Task-returning call under Assert.Throws is an un-awaited async exception; here the
        // throw happens on the inline path *before* the Task is returned, so Assert.Throws is correct.
#pragma warning disable xUnit2014
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            dispatcher.InvokeAsync(() => throw boom);
        });
#pragma warning restore xUnit2014
        Assert.Same(boom, thrown);
    }

    [AvaloniaFact]
    public void InvokeAsync_Marshaled_DoesNotFaultAndRoutesExceptionToSink()
    {
        Exception? observed = null;
        AvaloniaUiDispatcher dispatcher = new() { MarshaledExceptionSink = ex => observed = ex };
        InvalidOperationException boom = new("marshaled boom");

        Task invoke = null!;
        using ManualResetEventSlim posted = new(false);

        // Call InvokeAsync from a NON-UI thread so it takes the marshaled branch (CheckAccess() == false).
        // The worker returns the instant the job is queued — InvokeAsync posts, then returns tcs.Task.
        Thread worker = new(() =>
        {
            invoke = dispatcher.InvokeAsync(() => throw boom);
            posted.Set();
        })
        {
            IsBackground = true,
        };
        worker.Start();
        Assert.True(posted.Wait(TimeSpan.FromSeconds(5)), "InvokeAsync did not post within 5s");

        // The posted job hasn't run yet; draining it here (on the UI thread) runs the throwing action,
        // whose exception is routed to the sink, and the finally completes the TCS without faulting.
        Dispatcher.UIThread.RunJobs();
        worker.Join(TimeSpan.FromSeconds(5));

        Assert.True(invoke.IsCompleted);
        Assert.False(invoke.IsFaulted); // contract: the marshaled Task must NOT fault
        Assert.Equal(TaskStatus.RanToCompletion, invoke.Status);
        Assert.Same(boom, observed); // routed to the sink, not swallowed
    }

    [AvaloniaFact]
    public async Task CreateTimer_CreatedStopped_TicksOnlyAfterStart()
    {
        AvaloniaUiDispatcher dispatcher = new();
        int ticks = 0;
        IUiTimer timer = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(10), () => ticks++);
        try
        {
            // Created STOPPED (contract): letting the dispatcher run changes nothing.
            await PumpAsync(TimeSpan.FromMilliseconds(80));
            Assert.Equal(0, ticks);

            timer.Start();
            // Now it must tick. Generous budget; the loop exits on the first observed tick.
            for (int i = 0; i < 100 && ticks == 0; i++)
            {
                await PumpAsync(TimeSpan.FromMilliseconds(20));
            }

            Assert.True(ticks > 0, "timer should tick after Start()");

            timer.Stop();
            await PumpAsync(TimeSpan.FromMilliseconds(40)); // drain any tick already queued at Stop time
            int afterStop = ticks;
            await PumpAsync(TimeSpan.FromMilliseconds(120));
            Assert.Equal(afterStop, ticks); // no further ticks after Stop()
        }
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>Lets real time pass, then drains the dispatcher queue on the UI thread.</summary>
    private static async Task PumpAsync(TimeSpan wait)
    {
        await Task.Delay(wait);
        Dispatcher.UIThread.RunJobs();
    }
}
