using Avalonia.Threading;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IUiDispatcher"/>. Wraps <see cref="Dispatcher.UIThread"/>
/// and <see cref="DispatcherTimer"/>. Honours the asymmetric exception contract on
/// <see cref="IUiDispatcher.InvokeAsync"/>: the inline path (already on the UI thread) throws to the
/// awaiter, while the marshaled path routes exceptions to the framework's unhandled path
/// (<see cref="Dispatcher.UnhandledException"/>, wired by <c>App</c>) and the returned Task must NOT
/// fault. Avalonia's own <c>Dispatcher.UIThread.InvokeAsync</c> faults the task, so the marshaled
/// path is built on a plain <see cref="Dispatcher.Post(Action, DispatcherPriority)"/> plus a
/// <see cref="TaskCompletionSource"/> completed in a finally.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>
    /// Test seam: when set, a marshaled action's exception is routed here instead of propagating
    /// into the dispatcher loop, so a headless test can observe it without a live App
    /// <see cref="Dispatcher.UnhandledException"/> handler.
    /// </summary>
    internal Action<Exception>? MarshaledExceptionSink { get; init; }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action(); // inline path throws to the awaiter (contract)
            return Task.CompletedTask;
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (MarshaledExceptionSink is not null)
            {
                MarshaledExceptionSink(ex);
            }

            // Deliberately NO general catch: an unsunk marshaled exception propagates out of this
            // job and surfaces on Dispatcher.UIThread.UnhandledException, which App wires to log +
            // mark Handled (the contract's "framework unhandled path"). The finally still completes
            // the Task — it must NOT fault (contract).
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        // The DispatcherPriority-only ctor creates the timer STOPPED (only the 3-arg overload
        // auto-starts). Construction reads Dispatcher.UIThread statically and setting Interval on
        // a stopped timer touches no scheduling state, so this is safe off the UI thread; the
        // caller starts it on the UI thread via the returned wrapper.
        DispatcherTimer timer = new(DispatcherPriority.Background)
        {
            Interval = interval,
        };
        timer.Tick += (_, _) => onTick();
        return new AvaloniaUiTimer(timer);
    }

    private sealed class AvaloniaUiTimer(DispatcherTimer timer) : IUiTimer
    {
        public void Start() => timer.Start();

        public void Stop() => timer.Stop();

        public void Dispose() => timer.Stop();
    }
}
