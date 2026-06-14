// <copyright file="UploadScheduler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Threading.Channels;
using CSUploader.Lib;

namespace CSUploader.Upload;

/// <summary>
/// Channel-based single-consumer scheduler that serializes all state changes
/// and launches hashing/upload tasks within concurrency limits.
/// </summary>
public class UploadScheduler : IDisposable
{
    private readonly Channel<Action> _channel;
    private readonly AppSettings _settings;
    private readonly Pipeline.AttemptRunner _attemptRunner;
    private readonly IAppLogger _logger;
    private readonly Lib.Crypto.IHashingService _hashingService;
    private readonly Pipeline.IFileHosterRegistry _registry;
    private readonly List<Package> _packages = [];
    private readonly Lock _packagesLock = new();
    private Task? _loopTask;
    private CancellationTokenSource _loopCts = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadScheduler"/> class.
    /// </summary>
    /// <param name="settings">The application settings.</param>
    /// <param name="attemptRunner">The pipeline runner that executes one upload attempt.</param>
    /// <param name="logger">The application logger.</param>
    /// <param name="hashingService">The hashing service used to compute file checksums.</param>
    /// <param name="registry">The hoster registry used to look up per-hoster capabilities.</param>
    public UploadScheduler(AppSettings settings, Pipeline.AttemptRunner attemptRunner, IAppLogger logger, Lib.Crypto.IHashingService hashingService, Pipeline.IFileHosterRegistry registry)
    {
        _settings = settings;
        _attemptRunner = attemptRunner;
        _logger = logger;
        _hashingService = hashingService;
        _registry = registry;
        _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    /// <summary>
    /// Raised when a package is added to the scheduler.
    /// </summary>
    public event EventHandler<Package>? PackageAdded;

    /// <summary>
    /// Raised when a file's <see cref="FileState"/> changes.
    /// </summary>
    public event EventHandler<FileStateChangedEventArgs>? FileStateChanged;

    /// <summary>
    /// Gets a value indicating whether the scheduler is paused.
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Starts the scheduler's consumer loop. Idempotent - subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(ProcessLoopAsync);
    }

    /// <summary>
    /// Snapshot count of packages currently registered with the scheduler. Test-only —
    /// lets the autostart-mode tests assert whether LoadPersistedPackagesAsync registered
    /// the package or skipped it without poking through `_packages` reflectively.
    /// </summary>
    internal int RegisteredPackageCount
    {
        get
        {
            lock (_packagesLock)
            {
                return _packages.Count;
            }
        }
    }

    /// <summary>
    /// Adds a package and schedules its files. Idempotent — re-adding an existing package is a no-op.
    /// </summary>
    /// <param name="package">The package to add.</param>
    public void AddPackage(Package package) => AddPackage(package, scheduleIdleFiles: true);

    /// <summary>
    /// Registers a package with the scheduler. When <paramref name="scheduleIdleFiles"/>
    /// is true (the default), every Idle file in the package is queued and slots are
    /// filled — the right behaviour for bulk start / package load. Pass false when the
    /// caller wants to queue only a SPECIFIC file itself (the Uploads tab's per-row
    /// "Start"): the package is registered so the scheduler knows about it, but its other
    /// idle files are left untouched.
    /// </summary>
    public void AddPackage(Package package, bool scheduleIdleFiles)
    {
        lock (_packagesLock)
        {
            if (_packages.Contains(package))
            {
                return;
            }

            _packages.Add(package);
        }

        PackageAdded?.Invoke(this, package);
        if (scheduleIdleFiles)
        {
            Post(() => SchedulePackageFiles(package));
        }
    }

    /// <summary>
    /// Resumes scheduling and fills available slots. Requeues EVERY startable file across
    /// all packages — use for the global Start-all / resume actions. To start only a
    /// specific file/package that was already queued by the caller, use
    /// <see cref="FillAvailableSlots"/> instead so other idle files stay idle.
    /// </summary>
    public void StartAll()
    {
        Post(() =>
        {
            IsPaused = false;
            RequeueStartableFiles();
            FillSlots();
        });
    }

    /// <summary>
    /// Resumes scheduling (clears a global pause) and fills available hash/upload slots
    /// from files that are ALREADY in a queued state. Crucially, it does NOT requeue idle
    /// files — unlike <see cref="StartAll"/>, which pulls every idle/failed file across all
    /// packages into the queue. Use after manually transitioning a SPECIFIC package/file
    /// into a queued state (the Uploads tab's per-row Start / Reset) so only that work
    /// begins while every other idle file stays idle.
    /// </summary>
    public void FillAvailableSlots()
    {
        Post(() =>
        {
            // Un-pause so the just-queued file actually runs (matches the per-row Reset
            // behaviour), but skip RequeueStartableFiles so we don't sweep other idle
            // files into the queue.
            IsPaused = false;
            FillSlots();
        });
    }

    /// <summary>
    /// Pauses all running files by cancelling their tokens.
    /// </summary>
    public void PauseAll()
    {
        Post(() =>
        {
            IsPaused = true;
            PauseRunningFiles();
        });
    }

    /// <summary>
    /// Stops all files by cancelling their tokens and resetting state.
    /// </summary>
    public void StopAll() => Post(StopAllFiles);

    /// <summary>
    /// Removes a package from the scheduler.
    /// </summary>
    /// <param name="package">The package to remove.</param>
    public void RemovePackage(Package package) => Post(() => DoRemovePackage(package));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _loopCts.Cancel();

        // Wait briefly for the loop to drain pending actions
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown
        }

        _loopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetFileState(PackageFile file, FileState newState)
    {
        FileState old = file.State;
        if (old == newState)
        {
            return;
        }

        file.State = newState;
        FileStateChanged?.Invoke(this, new FileStateChangedEventArgs(file, old, newState));
    }

    private void Post(Action action)
    {
        if (!_channel.Writer.TryWrite(action))
        {
            System.Diagnostics.Debug.WriteLine("[UploadScheduler] Post failed — channel closed.");
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (Action action in _channel.Reader.ReadAllAsync(_loopCts.Token))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UploadScheduler] Action failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void SchedulePackageFiles(Package package)
    {
        foreach (PackageFile file in package)
        {
            if (file.State == FileState.Idle)
            {
                bool needsHash = _registry.Find(file.FileHoster.Name)?.RequiresHashingBeforeUpload ?? false;
                SetFileState(file, needsHash ? FileState.HashQueued : FileState.UploadQueued);
            }
        }

        FillSlots();
    }

    private void FillSlots()
    {
        if (IsPaused)
        {
            return;
        }

        PackageFile[] allFiles;
        lock (_packagesLock)
        {
            // Higher package priority is picked first. OrderByDescending is a stable
            // sort in LINQ-to-objects, so same-priority packages keep their insertion
            // order — no explicit tiebreaker needed.
            allFiles = [.. _packages.OrderByDescending(p => p.Priority).SelectMany(p => p)];
        }

        // Fill hashing slots
        int hashRunning = allFiles.Count(f => f.State == FileState.Hashing);
        int hashSlots = _settings.MaxConcurrentCPUJobs - hashRunning;
        foreach (PackageFile file in allFiles.Where(f => f.State == FileState.HashQueued).Take(Math.Max(0, hashSlots)))
        {
            LaunchHash(file);
        }

        // Fill upload slots
        int uploadRunning = allFiles.Count(f => f.State == FileState.Uploading);
        int uploadSlots = _settings.MaxConcurrentUploadJobs - uploadRunning;

        if (_settings.MaxUploadsPerHostEnabled)
        {
            var runningPerHost = allFiles
                .Where(f => f.State == FileState.Uploading)
                .GroupBy(f => f.FileHoster.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (PackageFile file in allFiles.Where(f => f.State == FileState.UploadQueued))
            {
                if (uploadSlots <= 0)
                {
                    break;
                }

                int hostRunning = runningPerHost.GetValueOrDefault(file.FileHoster.Name, 0);
                if (hostRunning >= _settings.MaxUploadsPerHost)
                {
                    continue;
                }

                LaunchUpload(file);
                runningPerHost[file.FileHoster.Name] = hostRunning + 1;
                uploadSlots--;
            }
        }
        else
        {
            foreach (PackageFile file in allFiles.Where(f => f.State == FileState.UploadQueued).Take(Math.Max(0, uploadSlots)))
            {
                LaunchUpload(file);
            }
        }
    }

    private void LaunchHash(PackageFile file)
    {
        SetFileState(file, FileState.Hashing);
        CancellationTokenSource cts = new();
        file.Cts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                string filePath = Path.Combine(file.SaveFrom ?? string.Empty, file.Name);
                await foreach (Lib.Crypto.HashEvent ev in _hashingService.HashFileAsync(filePath, System.Security.Cryptography.HashAlgorithmName.MD5, cts.Token))
                {
                    if (ev is Lib.Crypto.HashStarted)
                    {
                        file.StartedDate = DateTime.Now;
                    }
                    else if (ev is Lib.Crypto.HashProgress hp)
                    {
                        long remaining = hp.TotalBytes - hp.BytesProcessed;
                        file.Speed = (long)hp.SpeedBytesPerSec;
                        file.Progress = hp.PercentComplete;
                        file.BytesLoaded = hp.BytesProcessed;
                        file.BytesRemaining = remaining;
                        file.Duration = file.StartedDate.HasValue ? DateTime.Now - file.StartedDate.Value : null;
                        file.TimeRemaining = hp.SpeedBytesPerSec > 0 && remaining > 0
                            ? TimeSpan.FromSeconds(remaining / hp.SpeedBytesPerSec)
                            : null;
                    }
                    else if (ev is Lib.Crypto.HashCompleted hc)
                    {
                        file.FileHash = hc.HexHash;
                        file.IsHashingComplete = true;
                        file.Speed = null;
                        file.Progress = 0.0;
                        file.TimeRemaining = null;
                    }
                    else if (ev is Lib.Crypto.HashFailed hf)
                    {
                        file.Error = hf.Reason;
                        file.Speed = null;
                        _logger.Log(this, LogType.Error, $"Hashing failed for {file.Name}: {hf.Reason}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Post(() => OnHashCompleted(file, success: false, cancelled: true));
                return;
            }
            catch (Exception ex)
            {
                file.Error = ex.Message;
                _logger.Log(this, LogType.Error, $"Hashing pipeline crashed: {ex}");
                Post(() => OnHashCompleted(file, success: false));
                return;
            }

            Post(() => OnHashCompleted(file, success: file.IsHashingComplete));
        });
    }

    private void LaunchUpload(PackageFile file)
    {
        SetFileState(file, FileState.Uploading);
        CancellationTokenSource cts = new();
        file.Cts = cts;

        _ = Task.Run(async () =>
        {
            bool success = false;
            bool cancelled = false;
            bool crashed = false;
            Lib.Net.Http.HttpHandler? attemptHandler = null;
            try
            {
                await foreach (Pipeline.UploadEvent ev in _attemptRunner.RunAsync(file.BuildAttemptInputs(_logger), cts.Token))
                {
                    if (ev is Pipeline.HandlerBuilt hb)
                    {
                        attemptHandler = hb.Handler;
                    }

                    file.ApplyEvent(ev);
                    if (ev is Pipeline.AttemptFailed af)
                    {
                        _logger.Log(this, LogType.Error, $"Upload failed for {file.Name}: {af.Reason}");
                    }
                    else if (ev is Pipeline.AuthFailed authFailed)
                    {
                        _logger.Log(this, LogType.Error, $"Authentication failed for {file.Name}: {authFailed.Reason}");
                    }
                    else if (ev is Pipeline.AttemptCompleted ac)
                    {
                        success = ac.Success;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                file.Error = ex.Message;
                _logger.Log(this, LogType.Error, $"Upload pipeline crashed: {ex}");
                crashed = true;
            }
            finally
            {
                attemptHandler?.Dispose();
            }

            if (cancelled)
            {
                Post(() => OnUploadCompleted(file, success: false, cancelled: true));
                return;
            }

            if (crashed)
            {
                Post(() => OnUploadCompleted(file, success: false));
                return;
            }

            Post(() => OnUploadCompleted(file, success: success));
        });
    }

    private void OnHashCompleted(PackageFile file, bool success, bool cancelled = false)
    {
        DisposeCts(file);

        if (cancelled)
        {
            SetFileState(file, IsPaused ? FileState.Paused : FileState.Cancelled);
        }
        else if (!success)
        {
            SetFileState(file, FileState.Failed);
        }
        else
        {
            SetFileState(file, FileState.UploadQueued);
        }

        FillSlots();
    }

    private void OnUploadCompleted(PackageFile file, bool success, bool cancelled = false)
    {
        DisposeCts(file);

        if (cancelled)
        {
            SetFileState(file, IsPaused ? FileState.Paused : FileState.Cancelled);
        }
        else if (!success)
        {
            SetFileState(file, FileState.Failed);
        }
        else
        {
            SetFileState(file, FileState.Completed);
        }

        FillSlots();
    }

    private static void DisposeCts(PackageFile file)
    {
        file.Cts?.Dispose();
        file.Cts = null;
    }

    private void PauseRunningFiles()
    {
        PackageFile[] allFiles;
        lock (_packagesLock)
        {
            allFiles = [.. _packages.SelectMany(p => p)];
        }

        foreach (PackageFile file in allFiles)
        {
            if (file.State is FileState.Hashing or FileState.Uploading)
            {
                file.Cts?.Cancel();
                file.Cts?.Dispose();
                file.Cts = null;

                // State will transition to Paused in the completion callback
            }
            else if (file.State is FileState.HashQueued or FileState.UploadQueued)
            {
                SetFileState(file, FileState.Paused);
            }
        }
    }

    private void RequeueStartableFiles()
    {
        PackageFile[] allFiles;
        lock (_packagesLock)
        {
            allFiles = [.. _packages.SelectMany(p => p)];
        }

        foreach (PackageFile file in allFiles)
        {
            if (file.State is FileState.Idle or FileState.Paused or FileState.Cancelled or FileState.Failed)
            {
                // Clear any prior error so retries start fresh
                if (file.State == FileState.Failed)
                {
                    file.Error = null;
                }

                // Determine which queue the file should go into
                bool needsHash = _registry.Find(file.FileHoster.Name)?.RequiresHashingBeforeUpload ?? false;
                if (needsHash && !file.IsHashingComplete)
                {
                    SetFileState(file, FileState.HashQueued);
                }
                else
                {
                    SetFileState(file, FileState.UploadQueued);
                }
            }
        }
    }

    private void StopAllFiles()
    {
        IsPaused = false;

        PackageFile[] allFiles;
        lock (_packagesLock)
        {
            allFiles = [.. _packages.SelectMany(p => p)];
        }

        foreach (PackageFile file in allFiles)
        {
            if (file.State is FileState.Hashing or FileState.Uploading)
            {
                file.Cts?.Cancel();
                file.Cts?.Dispose();
            }

            if (file.State is not FileState.Completed and not FileState.Idle)
            {
                SetFileState(file, FileState.Cancelled);
            }

            file.Cts = null;
        }
    }

    private void DoRemovePackage(Package package)
    {
        // Cancel all running files
        foreach (PackageFile file in package)
        {
            file.Cts?.Cancel();
            file.Cts?.Dispose();
            file.Cts = null;
        }

        lock (_packagesLock)
        {
            _packages.Remove(package);
        }

        // If no packages remain and we were globally paused, reset the flag
        if (_packages.Count == 0)
        {
            IsPaused = false;
        }
    }
}
