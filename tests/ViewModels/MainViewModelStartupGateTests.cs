// <copyright file="MainViewModelStartupGateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// The startup gate: the handshake that lets a splash hold the main window back while an update
/// check runs, and settles what to do about anything it finds - ask, or install without asking -
/// before uploads can auto-start.
/// </summary>
public class StartupGateTests
{
    [Fact]
    public void ReleaseMainWindow_CompletesTheFirstSignalOnly()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);

        gate.ReleaseMainWindow();

        Assert.True(gate.MainWindowMayShow.IsCompletedSuccessfully);
        Assert.False(gate.MainWindowReady.IsCompleted);
    }

    /// <summary>
    /// Two signals, not one. "You may swap" says nothing about whether the swap HAPPENED, and the
    /// prompt cannot open until it has — it is owned by the window that does not exist yet.
    /// </summary>
    [Fact]
    public void MarkMainWindowReady_CompletesTheSecond()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);

        gate.ReleaseMainWindow();
        gate.MarkMainWindowReady();

        Assert.True(gate.MainWindowReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A transition that will never happen has to say so, or initialisation waits on it forever —
    /// holding a startup pipeline open against a process that is trying to exit.
    /// </summary>
    [Fact]
    public void Abandon_CancelsTheSecondSignal()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);

        gate.Abandon();

        Assert.True(gate.MainWindowReady.IsCanceled);
    }

    [Fact]
    public void BothSignals_AreIdempotent()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);

        gate.ReleaseMainWindow();
        gate.ReleaseMainWindow();
        gate.MarkMainWindowReady();
        gate.MarkMainWindowReady();
        gate.Abandon(); // after the fact, and must not throw

        Assert.True(gate.MainWindowReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// The race the return value exists for. Once either task completes the wait is OVER, and a
    /// cancellation arriving before the continuation runs cannot retroactively undo it — so a user
    /// who closes the splash in that window would otherwise get a main window shown for an app that
    /// has already decided to quit.
    /// </summary>
    [Fact]
    public async Task WaitToShowMainWindow_SaysNo_WhenAbandonedAfterTheWaitCompleted()
    {
        using CancellationTokenSource cts = new();
        StartupGate gate = new(TimeSpan.FromSeconds(5), cts.Token);

        gate.ReleaseMainWindow();   // the wait is now satisfied...
        cts.Cancel();               // ...and only then does the user close the splash

        Assert.False(await gate.WaitToShowMainWindowAsync(Task.CompletedTask));
    }

    [Fact]
    public async Task WaitToShowMainWindow_SaysYes_WhenTheGateReleasesNormally()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);
        gate.ReleaseMainWindow();

        Assert.True(await gate.WaitToShowMainWindowAsync(Task.CompletedTask));
    }

    /// <summary>
    /// Initialisation failing before it reaches the gate still ends the wait — otherwise nothing
    /// ever releases and the splash sits there forever. The fault is left for the caller that owns
    /// the task to observe; this only decides whether to show a window.
    /// </summary>
    [Fact]
    public async Task WaitToShowMainWindow_SaysYes_WhenInitialisationFailsBeforeTheGate()
    {
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);
        Task faulted = Task.FromException(new InvalidOperationException("no database"));

        // Bounded: without the initialisation arm this waits forever, and a hanging test says less
        // than a failing one.
        Assert.True(await gate.WaitToShowMainWindowAsync(faulted).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(gate.MainWindowMayShow.IsCompleted); // it was never released; the fault ended it
    }

    /// <summary>Cancelled while genuinely waiting, rather than after: that is a throw, not a false.</summary>
    [Fact]
    public async Task WaitToShowMainWindow_Throws_WhenAbandonedDuringTheWait()
    {
        using CancellationTokenSource cts = new();
        StartupGate gate = new(TimeSpan.FromSeconds(5), cts.Token);

        Task<bool> waiting = gate.WaitToShowMainWindowAsync(new TaskCompletionSource().Task);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
