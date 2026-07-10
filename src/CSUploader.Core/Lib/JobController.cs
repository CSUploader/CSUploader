// <copyright file="JobController.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

/// <summary>
/// Manages cancellation and pause tokens for a job.
/// </summary>
public class JobController : IDisposable
{
    private CancellationTokenSource _cts = new();
    private PauseTokenSource _pts = new();

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Gets a value indicating whether cancellation has been requested.
    /// </summary>
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    /// <summary>
    /// Gets the pause token.
    /// </summary>
    public PauseToken PauseToken => _pts.Token;

    /// <summary>
    /// Resets the controller with new cancellation and pause token sources.
    /// </summary>
    public void Reset()
    {
        _cts.Dispose();
        _pts.Dispose();
        _cts = new CancellationTokenSource();
        _pts = new PauseTokenSource();
    }

    /// <summary>
    /// Cancels the current job.
    /// </summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>
    /// Pauses the current job.
    /// </summary>
    /// <returns>The task.</returns>
    public Task PauseAsync() => _pts.PauseAsync();

    /// <summary>
    /// Resumes the current job.
    /// </summary>
    /// <returns>The task.</returns>
    public Task ResumeAsync() => _pts.ResumeAsync();

    /// <summary>
    /// Pauses if a pause has been requested.
    /// </summary>
    /// <returns>The task.</returns>
    public Task PauseIfRequestedAsync() => _pts.PauseIfRequestedAsync();

    /// <summary>
    /// Disposes the cancellation token source and pause token source.
    /// </summary>
    public void Dispose()
    {
        _cts.Dispose();
        _pts.Dispose();
        GC.SuppressFinalize(this);
    }
}
