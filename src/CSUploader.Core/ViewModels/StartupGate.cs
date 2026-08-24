// <copyright file="StartupGate.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.ViewModels;

/// <summary>
/// The two-way handshake between the splash and the initialisation running behind it.
/// </summary>
/// <remarks>
/// <para>
/// One signal is not enough. Initialisation must not open the update prompt until the real main
/// window exists to own it — and announcing "you may swap now" says nothing about whether the swap
/// has happened. So there are two: initialisation releases the head, waits for the head to confirm,
/// and only then asks anything.
/// </para>
/// <para>
/// Both use <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> so neither side runs
/// the other's continuation inline on its own stack, which would make the ordering depend on who
/// happened to complete first.
/// </para>
/// </remarks>
public sealed class StartupGate
{
    private readonly TaskCompletionSource _mainWindowMayShow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mainWindowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupGate(TimeSpan deadline, CancellationToken cancellationToken)
    {
        Deadline = deadline;
        CancellationToken = cancellationToken;
    }

    /// <summary>How long the splash may hold the main window back.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Cancelled when the splash is closed before the swap, which is terminal.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Completes when initialisation has finished or abandoned the update check.</summary>
    public Task MainWindowMayShow => _mainWindowMayShow.Task;

    /// <summary>Completes when the head has shown the real main window and closed the splash.</summary>
    public Task MainWindowReady => _mainWindowReady.Task;

    /// <summary>Called by initialisation from a cancellation-aware <c>finally</c>, so the head is
    /// stranded by nothing except a deliberate abandon.</summary>
    public void ReleaseMainWindow() => _mainWindowMayShow.TrySetResult();

    /// <summary>Called by the head once the real main window is up and the splash is gone.</summary>
    public void MarkMainWindowReady() => _mainWindowReady.TrySetResult();

    /// <summary>
    /// Called by the head when the transition cannot happen — the splash was closed, or showing the
    /// real window threw. Without it initialisation would wait for a swap that will never come.
    /// </summary>
    public void Abandon() => _mainWindowReady.TrySetCanceled();
}
