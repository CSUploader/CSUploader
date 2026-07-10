// <copyright file="IUiDispatcher.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Abstraction over UI-thread marshaling and UI-thread timers. The WPF head wraps
/// <c>Application.Current.Dispatcher</c> / <c>DispatcherTimer</c>; the Avalonia head
/// supplies its own. Lets the shared ViewModels marshal work to the UI thread and run
/// periodic ticks without referencing any framework's dispatcher type.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Fire-and-forget marshal to the UI thread (WPF Dispatcher.BeginInvoke).</summary>
    void Post(Action action);

    /// <summary>
    /// Marshals <paramref name="action"/> to the UI thread and completes once it has run.
    /// Runs inline when already on the UI thread, or when no UI thread exists.
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>Creates a STOPPED UI-thread timer; caller starts it.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action onTick);
}

/// <summary>A UI-thread timer that invokes the callback supplied at creation on each tick.</summary>
public interface IUiTimer : IDisposable
{
    void Start();

    void Stop();
}
