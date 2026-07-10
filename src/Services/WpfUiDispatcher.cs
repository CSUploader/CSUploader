// <copyright file="WpfUiDispatcher.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Threading;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IUiDispatcher"/>. Wraps <see cref="Application.Current"/>'s
/// <see cref="Dispatcher"/> and <see cref="DispatcherTimer"/>. Null-tolerant for headless tests,
/// preserving the ViewModels' former guards exactly: when <see cref="Application.Current"/> is null,
/// <see cref="Post"/> is a no-op (like the old <c>Application.Current?.Dispatcher.BeginInvoke</c>),
/// <see cref="CreateTimer"/> returns an inert timer (fixing the unconditional-<c>DispatcherTimer</c>
/// hazard), and <see cref="InvokeAsync"/> runs inline. On the live UI thread <see cref="InvokeAsync"/>
/// also runs inline (mirroring the former <c>CheckAccess()</c> fast paths).
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.BeginInvoke(action).Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return new NoOpTimer();
        }

        DispatcherTimer timer = new(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval,
        };
        timer.Tick += (_, _) => onTick();
        return new WpfUiTimer(timer);
    }

    private sealed class WpfUiTimer(DispatcherTimer timer) : IUiTimer
    {
        public void Start() => timer.Start();

        public void Stop() => timer.Stop();

        public void Dispose() => timer.Stop();
    }

    private sealed class NoOpTimer : IUiTimer
    {
        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
