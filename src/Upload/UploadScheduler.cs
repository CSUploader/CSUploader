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
    /// Force-starts the given files, launching each upload past the UPLOAD admission gate — the
    /// global upload limit and the per-host limit. The hashing/CPU limit is RESPECTED: a file
    /// that still needs hashing is queued for hashing through the normal gate and waits for a
    /// free CPU slot; only its upload jumps the limit (immediately after the hash finishes). The
    /// file enters the normal Uploading state, so <see cref="FillSlots"/> still COUNTS it when
    /// admitting normal files afterward: over-filling a slot suppresses the next normal admission
    /// until the running count drops back below the limit (it does not raise the limit). Files
    /// already running or Completed are left untouched. Honoured even while globally paused — only
    /// the named files run (after hashing, where required); the pause is not lifted.
    /// </summary>
    /// <param name="files">The files to force-start.</param>
    public void ForceStart(IEnumerable<PackageFile> files)
    {
        PackageFile[] snapshot = [.. files];
        Post(() =>
        {
            foreach (PackageFile file in snapshot)
            {
                ForceStartFile(file);
            }
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

    private void ForceStartFile(PackageFile file)
    {
        // Already running, or already done — nothing to force.
        if (file.State is FileState.Hashing or FileState.Uploading or FileState.Completed)
        {
            return;
        }

        // Mark so the upload launches immediately once the file is ready (see OnHashCompleted),
        // and so the flag is cleared on terminal completion. Clear any prior error so the
        // launched attempt starts fresh.
        file.ForceStart = true;
        file.Error = null;

        // Force start overrides the UPLOAD concurrency (global + per-host) but RESPECTS the
        // hashing/CPU limit. A hash-required file that hasn't hashed yet is queued for hashing
        // through the normal gate (HashQueued + FillSlots), so it waits for a free CPU slot like
        // any other file; once it hashes, OnHashCompleted launches its upload IMMEDIATELY — over
        // the upload limit — because ForceStart stays set. An already-hashed / no-hash file
        // launches its upload directly, bypassing FillSlots' upload gate. Either way the file
        // ends up in the normal Uploading state, so the next FillSlots counts it.
        bool needsHash = _registry.Find(file.FileHoster.Name)?.RequiresHashingBeforeUpload ?? false;
        if (needsHash && !file.IsHashingComplete)
        {
            SetFileState(file, FileState.HashQueued);
            FillSlots();
        }
        else
        {
            LaunchUpload(file);
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
            file.ForceStart = false;
            SetFileState(file, IsPaused ? FileState.Paused : FileState.Cancelled);
        }
        else if (!success)
        {
            file.ForceStart = false;
            SetFileState(file, FileState.Failed);
        }
        else if (file.ForceStart && file.State == FileState.Hashing)
        {
            // Force-started and STILL actively hashing — i.e. the user hasn't stopped/reset/
            // removed/paused it out from under us in the window between the hash finishing and
            // this callback running. Launch the upload immediately, over the limit. LaunchUpload
            // sets the state to Uploading; the FillSlots below counts it when deciding whether to
            // admit normal files. ForceStart is cleared by OnUploadCompleted on terminal.
            // The `State == Hashing` guard is belt-and-suspenders alongside the cancellation
            // paths that clear ForceStart: if either signal says "no longer force-hashing", we
            // fall through and queue normally (respecting the limit) instead of launching.
            LaunchUpload(file);
        }
        else
        {
            // Normal hash completion, or a force-started file whose work was cancelled during
            // hashing — clear any residual force flag and queue through the usual gate so we
            // never launch over the limit for a file the user is no longer force-starting.
            file.ForceStart = false;
            SetFileState(file, FileState.UploadQueued);
        }

        FillSlots();
    }

    private void OnUploadCompleted(PackageFile file, bool success, bool cancelled = false)
    {
        DisposeCts(file);

        // The force-start override is consumed once the upload reaches a terminal state, so a
        // later normal Start/Retry of this file goes through the usual admission gate.
        file.ForceStart = false;

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
                // Clear the force-start override: a paused file must not auto-launch its upload
                // when its in-flight hash completes in the cancellation window.
                file.ForceStart = false;
                file.Cts?.Cancel();
                file.Cts?.Dispose();
                file.Cts = null;

                // State will transition to Paused in the completion callback
            }
            else if (file.State is FileState.HashQueued or FileState.UploadQueued)
            {
                // A force-started file can sit in HashQueued waiting for a CPU slot — pausing it
                // must drop the override too, so a later normal resume doesn't bypass the upload
                // limit once it finally hashes.
                file.ForceStart = false;
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
            // Stopping clears any force-start override so a hash completing in the cancellation
            // window can't launch an upload for a file the user just stopped.
            file.ForceStart = false;

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
        // Cancel all running files. Clear ForceStart too: the file is leaving the scheduler,
        // so a hash completing in the cancellation window must not launch a detached upload.
        foreach (PackageFile file in package)
        {
            file.ForceStart = false;
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
