// <copyright file="InlineUiDispatcher.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Deterministic IUiDispatcher for ViewModel tests: Post and InvokeAsync run INLINE
/// (unlike WpfUiDispatcher, whose Post is a no-op without an Application), and timers
/// are manually tickable. This makes the VMs' Post-routed event handlers — the exact
/// path the Avalonia head will drive through a real dispatcher — testable.
/// </summary>
public sealed class InlineUiDispatcher : CSUploader.Services.IUiDispatcher
{
    public List<TestTimer> Timers { get; } = [];

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public CSUploader.Services.IUiTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        TestTimer timer = new(onTick);
        Timers.Add(timer);
        return timer;
    }

    public sealed class TestTimer(Action onTick) : CSUploader.Services.IUiTimer
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        /// <summary>Fires the tick callback if the timer is running.</summary>
        public void Tick()
        {
            if (IsRunning)
            {
                onTick();
            }
        }

        public void Dispose() => IsRunning = false;
    }
}
