// <copyright file="JobControllerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Tests.Lib;

public class JobControllerTests
{
    [Fact]
    public void InitialState_IsNotCancelled()
    {
        var controller = new JobController();

        Assert.False(controller.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_SetsIsCancellationRequested()
    {
        var controller = new JobController();

        controller.Cancel();

        Assert.True(controller.IsCancellationRequested);
    }

    [Fact]
    public void CancellationToken_IsNotCancelledInitially()
    {
        var controller = new JobController();

        Assert.False(controller.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_CancellationTokenReflectsCancellation()
    {
        var controller = new JobController();
        CancellationToken token = controller.CancellationToken;

        controller.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Reset_CreatesNewTokens_OldCancellationNoLongerApplies()
    {
        var controller = new JobController();
        controller.Cancel();
        Assert.True(controller.IsCancellationRequested);

        controller.Reset();

        Assert.False(controller.IsCancellationRequested);
        Assert.False(controller.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Reset_OldTokenStaysCancelled()
    {
        var controller = new JobController();
        CancellationToken oldToken = controller.CancellationToken;

        controller.Cancel();
        controller.Reset();

        // The old token should still be cancelled
        Assert.True(oldToken.IsCancellationRequested);
        // The new token should not be cancelled
        Assert.False(controller.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PauseIfRequestedAsync_ReturnsImmediately_WhenNotPaused()
    {
        var controller = new JobController();

        // Should complete without blocking
        Task task = controller.PauseIfRequestedAsync();
        bool completed = await Task.WhenAny(task, Task.Delay(1000)) == task;

        Assert.True(completed, "PauseIfRequestedAsync should return immediately when not paused");
    }

    [Fact]
    public async Task PauseAsync_And_ResumeAsync_WorkWithoutDeadlock()
    {
        var controller = new JobController();

        // Start a pause, which waits for PauseIfRequestedAsync to confirm
        Task pauseTask = controller.PauseAsync();

        // Simulate a worker calling PauseIfRequestedAsync (confirms the pause and waits for resume)
        Task workerTask = Task.Run(async () =>
        {
            await controller.PauseIfRequestedAsync();
        });

        // Wait for the pause to be confirmed
        bool pauseCompleted = await Task.WhenAny(pauseTask, Task.Delay(2000)) == pauseTask;
        Assert.True(pauseCompleted, "PauseAsync should complete after PauseIfRequestedAsync is called");

        // Resume
        await controller.ResumeAsync();

        // The worker should be released
        bool workerCompleted = await Task.WhenAny(workerTask, Task.Delay(2000)) == workerTask;
        Assert.True(workerCompleted, "Worker should be released after ResumeAsync");
    }

    [Fact]
    public async Task ResumeAsync_WhenNotPaused_CompletesImmediately()
    {
        var controller = new JobController();

        Task task = controller.ResumeAsync();
        bool completed = await Task.WhenAny(task, Task.Delay(1000)) == task;

        Assert.True(completed, "ResumeAsync should complete immediately when not paused");
    }

    [Fact]
    public async Task PauseAsync_CalledTwice_SecondCallReturnsImmediately()
    {
        var controller = new JobController();

        // First pause + confirm
        Task pauseTask = controller.PauseAsync();
        Task workerTask = Task.Run(async () => await controller.PauseIfRequestedAsync());
        await pauseTask;

        // Second PauseAsync while already paused should return immediately
        Task secondPause = controller.PauseAsync();
        bool completed = await Task.WhenAny(secondPause, Task.Delay(1000)) == secondPause;
        Assert.True(completed, "Second PauseAsync should return immediately when already paused");

        // Clean up: resume to release the worker
        await controller.ResumeAsync();
        await workerTask;
    }

    [Fact]
    public async Task Reset_AfterPause_AllowsFreshPauseCycle()
    {
        var controller = new JobController();

        controller.Reset();

        // After reset, PauseIfRequestedAsync should return immediately (no pause requested)
        Task task = controller.PauseIfRequestedAsync();
        bool completed = await Task.WhenAny(task, Task.Delay(1000)) == task;

        Assert.True(completed, "PauseIfRequestedAsync should return immediately after Reset");
    }
}
