// <copyright file="PackageDetails.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Upload;

/// <summary>
/// Abstract class for packages.
/// </summary>
public abstract class PackageDetails
{
    private JobController _jobController = new();

    /// <summary>
    /// Event triggered when the status changed.
    /// </summary>
    public event EventHandler<PackageStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Gets or sets the name of the package.
    /// </summary>
    public virtual string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size of the archive package.
    /// </summary>
    public virtual long? Size { get; protected set; }

    /// <summary>
    /// Gets or sets the file hosters used the package is uploading to.
    /// </summary>
    public virtual FileHosterClient[] FileHosters { get; protected set; } = [];

    /// <summary>
    /// Gets or sets the connection icons.
    /// </summary>
    public virtual string[] Connection { get; set; } = [];

    /// <summary>
    /// Gets or sets the gateway used for the package (i.e. the proxy ip:port and type (socks5, etc.)).
    /// </summary>
    public virtual string? Gateway { get; set; }

    /// <summary>
    /// Gets or sets the upload mode (free / registered / premium).
    /// </summary>
    public virtual UploadMode? UploadMode { get; set; }

    /// <summary>
    /// Gets the package status (idle, compressing, uploading, captcha, etc.)
    /// </summary>
    public virtual PackageStatus Status { get; private set; } = new();

    /// <summary>
    /// Gets or sets the error string if Status is Error.
    /// </summary>
    public virtual string? Error { get; set; }

    /// <summary>
    /// Gets or sets the bytes remaining of package to upload.
    /// </summary>
    public virtual long? BytesRemaining { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the package is enabled or disabled.
    /// </summary>
    public virtual bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the date the package was added (created).
    /// </summary>
    public virtual DateTime AddedDate { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets or sets the date the package started (uploading, compressing, etc.)
    /// </summary>
    public virtual DateTime? StartedDate { get; protected set; }

    /// <summary>
    /// Gets or sets the date the package finished uploading.
    /// </summary>
    public virtual DateTime? FinishedDate { get; set; }

    /// <summary>
    /// Gets or sets the duration the file is uploading (when uploading; pause/stopped/etc. time is not included).
    /// </summary>
    public virtual TimeSpan? Duration { get; set; }

    /// <summary>
    /// Gets or sets the job speed.
    /// </summary>
    public virtual long? Speed { get; set; }

    /// <summary>
    /// Gets or sets the time remaining until the job is complete.
    /// </summary>
    public virtual TimeSpan? TimeRemaining { get; set; }

    /// <summary>
    /// Gets or sets the bytes uploaded.
    /// </summary>
    public virtual long? BytesLoaded { get; set; }

    /// <summary>
    /// Gets or sets the progress left (in %).
    /// </summary>
    public virtual double? Progress { get; set; }

    /// <summary>
    /// Gets or sets the file path of the file on disk.
    /// </summary>
    public virtual string? SaveFrom { get; set; }

    /// <summary>
    /// Gets or sets the password of the package.
    /// </summary>
    public virtual string? Password { get; set; }

    /// <summary>
    /// Gets or sets the priority.
    /// </summary>
    /// <value>
    /// The priority.
    /// </value>
    public virtual int Priority { get; set; }

    /// <summary>
    /// Gets the next job.
    /// </summary>
    /// <returns>The next job, or null if finished.</returns>
    public abstract PackageJob? GetNextJob();

    /// <summary>
    /// Starts the job as an asynchronous operation.
    /// </summary>
    /// <param name="job">The job.</param>
    public void StartAsync(PackageJob job)
    {
        if (Status?.Status == JobStatus.Running)
        {
            return;
        }

        _jobController = new JobController();

        ChangeStatus(job, JobStatus.Running);

        Task.Run(async () =>
        {
            try
            {
                if (_jobController.IsCancellationRequested)
                {
                    _jobController.CancellationToken.ThrowIfCancellationRequested();
                }

                await _jobController.PauseIfRequestedAsync();

                await StartAsync(job, _jobController.PauseToken, _jobController.CancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                ChangeStatus(job, JobStatus.Cancelled);
            }
            catch (Exception ex)
            {
                ChangeStatus(job, ex.Message);
            }
        });
    }

    /// <summary>
    /// Pauses the job.
    /// </summary>
    public void PauseAsync(bool resume)
    {
        if (Status is null)
        {
            return;
        }

        ChangeStatus(Status.Job, resume ? JobStatus.Running : JobStatus.Paused);

        Task.Run(async () =>
        {
            try
            {
                if (resume)
                {
                    await _jobController.ResumeAsync();
                }
                else
                {
                    await _jobController.PauseAsync();
                }
            }
            catch (Exception)
            {
                // Pause/resume failures are non-fatal
            }
        });
    }

    /// <summary>
    /// Stops the current job as asynchronous operation.
    /// </summary>
    public void Stop()
    {
        _jobController.Cancel();
    }

    /// <summary>
    /// Changes the status.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="newStatus">The new status.</param>
    public void ChangeStatus(PackageJob job, JobStatus newStatus)
    {
        if (Status?.Job == job && Status?.Status == newStatus)
        {
            return;
        }

        JobStatus? previousStatus = Status?.Status;
        Status = new PackageStatus
        {
            Job = job,
            Status = newStatus
        };

        FireStatusChanged(job, previousStatus, newStatus);
    }

    /// <summary>
    /// Changes the status.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="error">The error.</param>
    public void ChangeStatus(PackageJob job, string error)
    {
        Error = error;

        ChangeStatus(job, JobStatus.Failed);
    }

    /// <summary>
    /// Starts the asynchronous.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="pauseToken">The pause token.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task StartAsync(PackageJob job, PauseToken pauseToken = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires the status changed.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="previousStatus">The previous status.</param>
    /// <param name="newStatus">The new status.</param>
    protected void FireStatusChanged(PackageJob job, JobStatus? previousStatus, JobStatus newStatus)
    {
        StatusChanged?.Invoke(this, new PackageStatusChangedEventArgs(this, job, previousStatus, newStatus));
    }

    /// <summary>
    /// Fires the status changed.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="PackageStatusChangedEventArgs"/> instance containing the event data.</param>
    protected void FireStatusChanged(object? sender, PackageStatusChangedEventArgs e)
    {
        StatusChanged?.Invoke(sender, e);
    }
}
