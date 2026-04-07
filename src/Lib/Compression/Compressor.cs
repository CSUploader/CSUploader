// <copyright file="Compressor.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Lib.Compression;

public abstract class Compressor
{
    private readonly CancellationTokenSource cancellationTokenSource = new();

    private readonly PauseTokenSource pauseTokenSource = new();

    /// <summary>
    /// Event when the status changed.
    /// </summary>
    public event EventHandler<JobStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Gets or sets the compression speed, in bytes.
    /// </summary>
    public virtual long? Speed { get; protected set; }

    /// <summary>
    /// Gets or sets the compression progress.
    /// </summary>
    public virtual double? Progress { get; protected set; }

    /// <summary>
    /// Gets or sets the bytes compressed.
    /// </summary>
    public virtual long? BytesCompressed { get; protected set; }

    /// <summary>
    /// Gets or sets the bytes remaining.
    /// </summary>
    public virtual long? BytesRemaining { get; protected set; }

    /// <summary>
    /// Gets or sets the size of all files in the given input directory, in bytes.
    /// </summary>
    public virtual long? Size { get; protected set; }

    /// <summary>
    /// Gets or sets the elapsed time since compression has started.
    /// </summary>
    public virtual TimeSpan? TimeElapsed { get; protected set; }

    /// <summary>
    /// Gets or sets the remaining time until compression is done.
    /// </summary>
    public virtual TimeSpan? TimeRemaining { get; protected set; }

    /// <summary>
    /// Gets or sets the compressor status.
    /// </summary>
    public virtual JobStatus? Status { get; protected set; }

    /// <summary>
    /// Gets or sets the error string if Status is Error.
    /// </summary>
    public virtual string? Error { get; set; }

    public void CompressAsync(string inputDirectoryPath, string outputDirectoryPath)
    {
        Task.Run(async () =>
        {
            try
            {
                await CompressAsync(inputDirectoryPath, outputDirectoryPath, pauseTokenSource.Token, cancellationTokenSource.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                ChangeStatus(JobStatus.Cancelled);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ChangeStatus(JobStatus.Failed);
            }
        });
    }

    /// <summary>
    /// Pauses the job.
    /// </summary>
    public void PauseAsync(bool resume)
    {
        ChangeStatus(resume ? JobStatus.Running : JobStatus.Paused);

        Task.Run(async () =>
        {
            if (resume)
            {
                await pauseTokenSource.ResumeAsync();
            }
            else
            {
                await pauseTokenSource.PauseAsync();
            }
        });
    }

    /// <summary>
    /// Stops the current job as asynchronous operation.
    /// </summary>
    public void StopAsync()
    {
        cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Changes the status.
    /// </summary>
    /// <param name="newStatus">The new status.</param>
    public void ChangeStatus(JobStatus newStatus)
    {
        if (Status.HasValue && Status.Value == newStatus)
        {
            return;
        }

        JobStatus? previousStatus = Status;
        Status = newStatus;

        FireStatusChanged(previousStatus, newStatus);
    }

    /// <summary>
    /// Compresses the given input directory path to the output directory path.
    /// </summary>
    /// <param name="inputDirectoryPath">The input directory path.</param>
    /// <param name="outputDirectoryPath">The output directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="pauseToken">The pause token.</param>
    /// <returns>The task.</returns>
    public abstract Task CompressAsync(string inputDirectoryPath, string outputDirectoryPath, PauseToken pauseToken = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires the status changed.
    /// </summary>
    /// <param name="previousStatus">The previous status.</param>
    /// <param name="newStatus">The new status.</param>
    protected void FireStatusChanged(JobStatus? previousStatus, JobStatus newStatus)
    {
        StatusChanged?.Invoke(this, new JobStatusChangedEventArgs(previousStatus, newStatus));
    }

    /// <summary>
    /// Fires the status changed.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="JobStatusChangedEventArgs"/> instance containing the event data.</param>
    protected void FireStatusChanged(object sender, JobStatusChangedEventArgs e)
    {
        StatusChanged?.Invoke(sender, e);
    }
}
