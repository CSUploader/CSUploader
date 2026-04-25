// <copyright file="PauseTokenSource.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

public class PauseTokenSource : IDisposable
{
    private readonly SemaphoreSlim stateAsyncLock = new(1);
    private readonly SemaphoreSlim pauseRequestAsyncLock = new(1);
    private bool _disposed;

    private bool paused = false;
    private bool pauseRequested = false;

    private TaskCompletionSource<bool>? resumeRequestTcs;
    private TaskCompletionSource<bool>? pauseConfirmationTcs;

    public PauseToken Token
    {
        get { return new PauseToken(this); }
    }

    public async Task<bool> IsPaused(CancellationToken token = default)
    {
        await stateAsyncLock.WaitAsync(token);
        try
        {
            return paused;
        }
        finally
        {
            stateAsyncLock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken token = default)
    {
        await stateAsyncLock.WaitAsync(token);

        try
        {
            if (!paused)
            {
                return;
            }

            await pauseRequestAsyncLock.WaitAsync(token);
            try
            {
                TaskCompletionSource<bool>? resumeRequestTcs = this.resumeRequestTcs;
                paused = false;
                pauseRequested = false;
                this.resumeRequestTcs = null;
                pauseConfirmationTcs = null;
                resumeRequestTcs?.TrySetResult(true);
            }
            finally
            {
                pauseRequestAsyncLock.Release();
            }
        }
        finally
        {
            stateAsyncLock.Release();
        }
    }

    public async Task PauseAsync(CancellationToken token = default)
    {
        await stateAsyncLock.WaitAsync(token);

        try
        {
            if (paused)
            {
                return;
            }

            Task? pauseConfirmationTask = null;

            await pauseRequestAsyncLock.WaitAsync(token);
            try
            {
                pauseRequested = true;
                resumeRequestTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pauseConfirmationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pauseConfirmationTask = WaitForPauseConfirmationAsync(token);
            }
            finally
            {
                pauseRequestAsyncLock.Release();
            }

            await pauseConfirmationTask;

            paused = true;
        }
        finally
        {
            stateAsyncLock.Release();
        }
    }

    public async Task PauseIfRequestedAsync(CancellationToken token = default)
    {
        Task? resumeRequestTask = null;

        await pauseRequestAsyncLock.WaitAsync(token);

        try
        {
            if (!pauseRequested)
            {
                return;
            }

            resumeRequestTask = WaitForResumeRequestAsync(token);
            pauseConfirmationTcs?.TrySetResult(true);
        }
        finally
        {
            pauseRequestAsyncLock.Release();
        }

        if (resumeRequestTask != null)
        {
            await resumeRequestTask;
        }
    }

    private async Task WaitForResumeRequestAsync(CancellationToken token)
    {
        if (resumeRequestTcs == null)
        {
            return;
        }

        using (token.Register(() => resumeRequestTcs.TrySetCanceled(), useSynchronizationContext: false))
        {
            await resumeRequestTcs.Task;
        }
    }

    private async Task WaitForPauseConfirmationAsync(CancellationToken token)
    {
        if (pauseConfirmationTcs == null)
        {
            return;
        }

        using (token.Register(() => pauseConfirmationTcs.TrySetCanceled(), useSynchronizationContext: false))
        {
            await pauseConfirmationTcs.Task;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        stateAsyncLock.Dispose();
        pauseRequestAsyncLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
