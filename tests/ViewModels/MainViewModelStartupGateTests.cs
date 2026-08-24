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
/// check runs, and asks about anything it finds before uploads can auto-start.
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
}

/// <summary>
/// The gate driven through the real <c>MainViewModel</c>: what the user is asked, when, and what
/// happens on each of the five ways a startup check can end.
/// </summary>
public class MainViewModelStartupGateTests : IDisposable
{
    private readonly List<MainViewModel> _vms = [];

    public void Dispose()
    {
        foreach (MainViewModel vm in _vms)
        {
            vm.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakePrompt : IStartupUpdatePrompt
    {
        public FakePrompt(StartupUpdatePromptResult answer) => Answer = answer;

        public StartupUpdatePromptResult Answer { get; }

        public int Shown { get; private set; }

        public string? NewVersion { get; private set; }

        public Task<StartupUpdatePromptResult> ShowAsync(string newVersion, string currentVersion, bool askAtStartup)
        {
            Shown++;
            NewVersion = newVersion;
            return Task.FromResult(Answer);
        }
    }
}
