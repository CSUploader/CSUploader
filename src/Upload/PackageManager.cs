// <copyright file="PackageManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;

namespace CSUploader.Upload;

/// <summary>
/// Thin wrapper around <see cref="UploadScheduler"/> that maintains the public API
/// consumed by ViewModels, with database persistence for packages and file states.
/// </summary>
public class PackageManager
{
    private readonly AppSettings _settings;
    private readonly UploadScheduler _scheduler;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly IAppLogger _logger;
    private readonly Pipeline.IFileHosterRegistry _registry;
    private readonly Lock _lock = new();

    // Serializes state-change persistence so that when PackageCompleted fires for the last file,
    // every prior file's UpdateStateAsync (and its URL) has already been committed to SQLite.
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    // Tracks every in-flight fire-and-forget persistence task so DrainPendingPersistenceAsync can
    // await ALL of them — including a Task.Run that has been scheduled but hasn't yet started (and
    // so hasn't taken _persistLock). Registering the task here happens synchronously on the
    // event-firing thread BEFORE the work is dispatched, closing the window where the drain would
    // otherwise acquire the free lock and return while a queued callback was still pending, letting
    // its EF Core write race the SqliteConnection's dispose. See tests/CLAUDE.md.
    private readonly HashSet<Task> _pendingPersistence = [];
    private readonly Lock _pendingPersistenceLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageManager"/> class.
    /// </summary>
    /// <param name="settings">The application settings.</param>
    /// <param name="scheduler">The upload scheduler.</param>
    /// <param name="packageRepo">The upload package repository.</param>
    /// <param name="fileRepo">The upload package file repository.</param>
    /// <param name="loginRepo">The file hoster login repository.</param>
    /// <param name="logger">The application logger.</param>
    /// <param name="registry">The file hoster registry used to look up per-hoster capabilities.</param>
    public PackageManager(
        AppSettings settings,
        UploadScheduler scheduler,
        UploadPackageRepository packageRepo,
        UploadPackageFileRepository fileRepo,
        FileHosterLoginRepository loginRepo,
        IAppLogger logger,
        Pipeline.IFileHosterRegistry registry)
    {
        _settings = settings;
        _scheduler = scheduler;
        _packageRepo = packageRepo;
        _fileRepo = fileRepo;
        _loginRepo = loginRepo;
        _logger = logger;
        _registry = registry;

        _scheduler.PackageAdded += (_, package) => PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));
        _scheduler.FileStateChanged += OnFileStateChanged;
        _scheduler.QueueOrderChanged += OnQueueOrderChanged;
        _scheduler.Start();

        // No need to subscribe to ProxyManager.RotationReloaded — RapidgatorClient (and
        // any future hoster) builds its HttpHandler at the start of each upload attempt,
        // so a queued file naturally picks up the latest proxy choice on its next run.
    }

    /// <summary>
    /// Raised when a package is added.
    /// </summary>
    public event EventHandler<PackageAddedEventArgs>? PackageAdded;

    /// <summary>
    /// Raised after all files in a package have completed and the DB flag has been updated.
    /// </summary>
    public event EventHandler<Package>? PackageCompleted;

    /// <summary>
    /// Raised after an individual file reaches a terminal state (Completed/Failed/Cancelled)
    /// and its state has been persisted. Lets the Uploaded tab refresh per-file, without
    /// waiting for the whole package to finish.
    /// </summary>
    public event EventHandler<PackageFile>? FileCompleted;

    /// <summary>
    /// Gets the list of packages.
    /// </summary>
    public List<Package> Packages { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the scheduler is paused.
    /// </summary>
    public bool IsPaused => _scheduler.IsPaused;

    /// <summary>
    /// Creates a package from the given options, adds it, and starts scheduling.
    /// </summary>
    /// <param name="options">The package options.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task AddAndStartPackageAsync(PackageOptions options)
    {
        Package package = await CreatePackageAsync(options);
        _scheduler.AddPackage(package);
    }

    /// <summary>
    /// Creates and registers a package without scheduling it for upload.
    /// Files are added and the UI is notified, but no hashing/uploading starts.
    /// Call <see cref="SchedulePackage"/> later to start it.
    /// </summary>
    /// <returns>The created package.</returns>
    public async Task<Package> AddPackageOnlyAsync(PackageOptions options)
    {
        Package package = await CreatePackageAsync(options);
        PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));
        return package;
    }

    /// <summary>
    /// Schedules a previously-added package for upload.
    /// </summary>
    public void SchedulePackage(Package package) => _scheduler.AddPackage(package);

    /// <summary>
    /// Schedules a delayed start for a package at the specified time.
    /// </summary>
    /// <param name="package">The package to schedule.</param>
    /// <param name="scheduledAt">The date/time to start the package.</param>
    public void ScheduleDelayedStart(Package package, DateTime scheduledAt)
    {
        package.ScheduledStartTime = scheduledAt;
        TimeSpan delay = scheduledAt - DateTime.Now;
        if (delay <= TimeSpan.Zero)
        {
            _scheduler.AddPackage(package);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            _scheduler.AddPackage(package);
        });
    }

    /// <summary>
    /// Loads all persisted packages, including completed ones, so the Uploads tab keeps
    /// showing finished work after a restart. Resumes scheduling for any non-terminal
    /// packages. When <see cref="AppSettings.RemoveFinishedUploads"/> is
    /// <see cref="RemoveFinishedUploadsMode.AtStartup"/>, fully-successful packages are
    /// soft-removed from the Uploads tab here so the user starts each session with a
    /// clean queue.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task LoadPersistedPackagesAsync()
    {
        try
        {
            UploadPackageDto[] all = await _packageRepo.GetAllAsync();

            foreach (UploadPackageDto pkgDto in all)
            {
                try
                {
                    await LoadOnePersistedPackageAsync(pkgDto);
                }
                catch (Exception ex)
                {
                    // Per-package isolation: a single bad row (e.g. a Completed file
                    // whose source has been deleted, throwing inside FileInfo.Length)
                    // must not abort the whole load. Log and move on.
                    _logger.Log(this, LogType.Error, $"Skipped persisted package id={pkgDto.Id} ({pkgDto.Name}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to load persisted packages: {ex.Message}");
        }
    }

    private async Task LoadOnePersistedPackageAsync(UploadPackageDto pkgDto)
    {
        // Skip packages that the user has soft-removed from the Uploads tab.
        // Their per-file rows may still be visible on the Uploaded tab.
        if (pkgDto.IsRemovedFromUploads)
        {
            return;
        }

        if (pkgDto.Files is null || pkgDto.Files.Count == 0)
        {
            return;
        }

        // Drop individual file rows the user removed from Uploads (the package
        // itself was kept). They stay queryable for the Uploaded tab via IsHidden.
        pkgDto.Files = [.. pkgDto.Files.Where(f => !f.IsRemovedFromUploads)];
        if (pkgDto.Files.Count == 0)
        {
            return;
        }

        // Build FileHosterLogins dictionary (one resolved login per hoster name).
        // resolvedLogins is also reused by the file-reconstruction loop below so
        // every PackageFile for the same hoster shares the same login instance —
        // and we only hit the DB once per hoster instead of once per file.
        Dictionary<FileHosterClient, FileHosterLoginDto> fileHosterLogins = [];
        Dictionary<string, FileHosterLoginDto> resolvedLogins = new(StringComparer.Ordinal);

        foreach (UploadPackageFileDto fileDto in pkgDto.Files)
        {
            string hosterName = fileDto.FileHosterName ?? fileDto.FileHoster ?? string.Empty;
            if (string.IsNullOrEmpty(hosterName) || fileHosterLogins.Keys.Any(k => string.Equals(k.Name, hosterName, StringComparison.Ordinal)))
            {
                continue;
            }

            var client = FileHosterClient.FindByHost(hosterName, Protocol.Http, _logger);
            if (client is null)
            {
                continue;
            }

            FileHosterLoginDto? login = null;
            if (fileDto.FileHosterLoginId > 0)
            {
                login = await _loginRepo.FindAsync(fileDto.FileHosterLoginId);
                if (login is null)
                {
                    _logger.Log(this, LogType.Error, $"Persisted FileHosterLoginId={fileDto.FileHosterLoginId} for {hosterName} could not be found in the accounts table; the account may have been deleted (uploads will fail).");
                }
            }

            // FileHosterLoginId == 0 is the wizard's built-in Anonymous selection — it has no
            // saved account row, so reconstitute it as an anonymous credential and the pipeline
            // takes its no-login upload path (otherwise an anonymous package reloaded after a
            // restart fails with "no API key / username"). A positive id that simply wasn't
            // found (account deleted) stays non-anonymous and fails, which is correct.
            login ??= new FileHosterLoginDto { FileHosterName = hosterName, IsAnonymous = fileDto.FileHosterLoginId == 0 };

            fileHosterLogins[client] = login;
            resolvedLogins[hosterName] = login;
        }

        if (fileHosterLogins.Count == 0)
        {
            return;
        }

        // Reconstruct Package
        PackageOptions options = new()
        {
            Title = pkgDto.Name ?? string.Empty,
            Logger = _logger,
            FileHosters = fileHosterLogins,
            Settings = _settings,
        };
        Package package = new(options)
        {
            DbId = pkgDto.Id,
            ScheduledStartTime = pkgDto.ScheduledStartTime,
            SpeedLimitKBps = pkgDto.SpeedLimitKBps,
        };

        // Override Name since it was persisted
        package.Name = pkgDto.Name ?? package.Name;

        // Track whether any file was in a non-paused/cancelled/terminal state at
        // last shutdown. Drives the Autostart Uploads "Only if running at last
        // session's end" gate below.
        bool wasRunningAtShutdown = false;

        // Reconstruct PackageFiles
        List<PackageFile> files = [];
        foreach (UploadPackageFileDto fileDto in pkgDto.Files)
        {
            if (fileDto.State is FileState.Idle or FileState.HashQueued or FileState.Hashing
                or FileState.UploadQueued or FileState.Uploading)
            {
                wasRunningAtShutdown = true;
            }

            string hosterName = fileDto.FileHosterName ?? fileDto.FileHoster ?? string.Empty;
            var client = FileHosterClient.FindByHost(hosterName, Protocol.Http, _logger);
            if (client is null)
            {
                continue;
            }

            // Reuse the login already resolved (with logging) by the first loop —
            // ensures every file for this hoster ends up with the same credentials.
            if (!resolvedLogins.TryGetValue(hosterName, out FileHosterLoginDto? login))
            {
                login = new FileHosterLoginDto { FileHosterName = hosterName };
            }

            string filePath = Path.Combine(fileDto.FileDirectory ?? string.Empty, fileDto.FileName ?? string.Empty);

            // No disk-existence check here: the runtime path (HashingService /
            // HttpHandler.UploadFileAsync) already surfaces missing files as Failed with
            // a real error message. Doing the same check at load time just produced
            // duplicate noise on every restart for files the user hadn't started yet.
            PackageFile pf = new(package, filePath, client, login)
            {
                DbId = fileDto.Id,
                IsHashingComplete = fileDto.IsHashingComplete,
                FileHash = fileDto.FileHash,
                StartedDate = fileDto.StartDateTime > DateTime.MinValue ? fileDto.StartDateTime : null,
                FinishedDate = fileDto.FinishedDateTime > DateTime.MinValue ? fileDto.FinishedDateTime : null,
                QueueOrder = fileDto.QueueOrder,
            };

            // Source file may have been deleted between sessions for terminal-state
            // rows. Restore the persisted size so the History row still has it.
            if (pf.Size is null && fileDto.FileSize > 0)
            {
                pf.Size = fileDto.FileSize;
            }

            // Restore Duration too — derived from Start/Finish so the column doesn't
            // show 00s for completed rows that finished in a prior session.
            if (pf.StartedDate is { } start && pf.FinishedDate is { } finish && finish > start)
            {
                pf.Duration = finish - start;
            }

            // Remap state: interrupted Hashing/Uploading -> re-queue.
            // Idle and Uploading map to HashQueued when hashing is required and
            // not yet complete, so the file always has a valid hash before upload.
            bool needsHash = _registry.Find(hosterName)?.RequiresHashingBeforeUpload ?? false;
            FileState restoredState = fileDto.State switch
            {
                FileState.Hashing => FileState.HashQueued,
                FileState.Uploading => needsHash && !pf.IsHashingComplete
                    ? FileState.HashQueued
                    : FileState.UploadQueued,
                FileState.Idle => needsHash && !pf.IsHashingComplete
                    ? FileState.HashQueued
                    : FileState.UploadQueued,
                _ => fileDto.State,
            };
            pf.State = restoredState;
            pf.Error = fileDto.Error;
            pf.FileUrl = fileDto.FileUrl;

            files.Add(pf);
        }

        if (files.Count == 0)
        {
            return;
        }

        bool allTerminal = files.TrueForAll(f => f.State is FileState.Completed or FileState.Failed or FileState.Cancelled);
        bool allSuccessful = files.Count > 0 && files.TrueForAll(f => f.State == FileState.Completed);

        // AtStartup mode: drop fully-successful packages from the Uploads tab on
        // app launch. The Uploaded tab still shows the row via its own query —
        // soft-remove only flips the IsRemovedFromUploads flag.
        if (allSuccessful && _settings.RemoveFinishedUploads == RemoveFinishedUploadsMode.AtStartup)
        {
            await _packageRepo.SoftRemoveFromUploadsAsync(pkgDto.Id);
            return;
        }

        package.AddPackageFiles([.. files]);

        lock (_lock)
        { Packages.Add(package); }
        PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));

        if (allTerminal)
        {
            // All files reached a terminal state already. Keep the package on the
            // Uploads tab (so the user can see / Remove it manually) but don't try
            // to schedule it for further work; just keep the DB flag in sync.
            await _packageRepo.UpdateCompletedFlagAsync(pkgDto.Id, true);
            return;
        }

        // Honour the Autostart Uploads policy. Never → leave the package idle
        // (user can click Start). OnlyIfRunningAtLastSession → resume only if a
        // file was active when the app shut down. Always → resume unconditionally.
        bool shouldAutostart = _settings.AutostartUploads switch
        {
            AutostartUploadsMode.Never => false,
            AutostartUploadsMode.Always => true,
            AutostartUploadsMode.OnlyIfRunningAtLastSession => wasRunningAtShutdown,
            _ => false,
        };

        bool hasQueuedFiles = files.Any(f => f.State is FileState.HashQueued or FileState.UploadQueued);
        if (shouldAutostart && hasQueuedFiles)
        {
            if (pkgDto.ScheduledStartTime is not null && pkgDto.ScheduledStartTime > DateTime.Now)
            {
                ScheduleDelayedStart(package, pkgDto.ScheduledStartTime.Value);
            }
            else
            {
                _scheduler.AddPackage(package);
            }
        }
    }

    private async Task<Package> CreatePackageAsync(PackageOptions options)
    {
        Package package = new(options);

        // Filter (file, hoster) pairs at queue time when the file would exceed the
        // hoster's declared per-file size cap. Without this, the pipeline's pre-check
        // would still fail those attempts at runtime — but each one would first show
        // up as a row in the Uploads grid, transition to Failed, and force the user
        // to manually clean them up. Surface the registry to AddPackageFiles so the
        // filter can apply.
        package.AddPackageFiles(_registry, _logger);

        lock (_lock)
        {
            Packages.Add(package);
        }

        await PersistNewPackageAsync(package);

        return package;
    }

    private async Task PersistNewPackageAsync(Package package)
    {
        try
        {
            UploadPackageDto pkgDto = new()
            {
                Name = package.Name,
                CreatedDateTime = DateTime.Now,
                ScheduledStartTime = package.ScheduledStartTime,
                IsCompleted = false,
                SpeedLimitKBps = package.SpeedLimitKBps,
                StartMode = UploadStartMode.Immediately,
            };
            await _packageRepo.InsertAsync(pkgDto);
            package.DbId = pkgDto.Id;

            int sortOrder = 0;
            foreach (PackageFile file in package)
            {
                UploadPackageFileDto fileDto = new()
                {
                    FileName = file.Name,
                    FileDirectory = file.SaveFrom ?? string.Empty,
                    FileSize = file.Size ?? 0,
                    FileHoster = file.FileHoster.Name,
                    FileHosterName = file.FileHoster.Name,
                    // Denormalize the account name (username for a real account; null for anonymous,
                    // which the History tab renders as the localized "(anonymous)" via FileHosterLoginId==0).
                    FileHosterAccount = file.FileHosterLogin is { IsAnonymous: false } login ? login.Username : null,
                    StartDateTime = DateTime.Now,
                    State = file.State,
                    IsHashingComplete = file.IsHashingComplete,
                    FileHash = file.FileHash,
                    FileHosterLoginId = file.FileHosterLogin?.Id ?? 0,
                    SortOrder = sortOrder++,
                    PackageId = package.DbId.Value,
                    QueueOrder = file.QueueOrder,
                };
                await _fileRepo.InsertAsync(fileDto);
                file.DbId = fileDto.Id;
            }
        }
        catch (Exception ex)
        {
            string detail = ex.Message;
            Exception? inner = ex.InnerException;
            while (inner is not null)
            {
                detail += " | inner: " + inner.Message;
                inner = inner.InnerException;
            }

            _logger.Log(this, LogType.Error, $"Failed to persist package: {detail}");
        }
    }

    private void OnFileStateChanged(object? sender, FileStateChangedEventArgs e)
    {
        if (e.File.DbId is null)
        {
            return;
        }

        int fileId = e.File.DbId.Value;
        FileState state = e.NewState;
        string? error = e.File.Error;
        string? fileUrl = e.File.FileUrl;
        // Hashing → next-state transition is the natural "hash now valid" moment. Capture
        // the hash here (string is interned-cheap) and persist alongside the state change.
        string? fileHashIfJustComputed = e.OldState == FileState.Hashing
                && e.File.IsHashingComplete
                && !string.IsNullOrEmpty(e.File.FileHash)
            ? e.File.FileHash
            : null;
        DateTime? finishedDateTime = state is FileState.Completed or FileState.Failed or FileState.Cancelled
            ? (e.File.FinishedDate ?? DateTime.Now)
            : null;

        TrackPersistence(async () =>
        {
            await _persistLock.WaitAsync();
            try
            {
                await _fileRepo.UpdateStateAsync(fileId, (int)state, error, fileUrl, finishedDateTime);

                if (fileHashIfJustComputed is not null)
                {
                    await _fileRepo.UpdateHashAsync(fileId, fileHashIfJustComputed);
                }

                bool isTerminal = state is FileState.Completed or FileState.Failed or FileState.Cancelled;
                if (isTerminal)
                {
                    FileCompleted?.Invoke(this, e.File);

                    if (e.File.Package.DbId is not null)
                    {
                        bool allDone = true;
                        foreach (PackageFile f in e.File.Package)
                        {
                            if (f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
                            {
                                allDone = false;
                                break;
                            }
                        }

                        if (allDone)
                        {
                            await _packageRepo.UpdateCompletedFlagAsync(e.File.Package.DbId.Value, true);
                            PackageCompleted?.Invoke(this, e.File.Package);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to persist state: {ex.Message}");
            }
            finally
            {
                _persistLock.Release();
            }
        });
    }

    private void OnQueueOrderChanged(object? sender, IReadOnlyList<PackageFile> files)
    {
        Dictionary<int, int> orders = files
            .Where(f => f.DbId is not null)
            .ToDictionary(f => f.DbId!.Value, f => f.QueueOrder);
        if (orders.Count == 0)
        {
            return;
        }

        TrackPersistence(async () =>
        {
            await _persistLock.WaitAsync();
            try
            {
                await _fileRepo.UpdateQueueOrderAsync(orders);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to persist queue order: {ex.Message}");
            }
            finally
            {
                _persistLock.Release();
            }
        });
    }

    /// <summary>Moves a file to 1-based position <paramref name="target"/> in the global upload queue.</summary>
    public void MoveFileTo(PackageFile file, int target) => _scheduler.MoveFileTo(file, target);

    /// <summary>Moves the given files as a block by <paramref name="delta"/> positions (negative = sooner).</summary>
    public void MoveFilesBy(IReadOnlyList<PackageFile> files, int delta) => _scheduler.MoveFilesBy(files, delta);

    /// <summary>
    /// Test/shutdown helper: waits until any in-flight fire-and-forget persistence callback
    /// (queued by <see cref="OnFileStateChanged"/>, <see cref="OnQueueOrderChanged"/>, or
    /// <see cref="RemovePackage"/>) has finished. Callers should stop the source of new
    /// state-change events first (e.g.
    /// <c>scheduler.Dispose()</c>); this only drains what is currently in flight, not what
    /// arrives next. Used by xUnit test fixtures to keep lingering writes from racing into
    /// the next test against a disposed <c>SqliteConnection</c>.
    /// </summary>
    internal async Task DrainPendingPersistenceAsync()
    {
        // Snapshot and await every tracked persistence task. A task that was scheduled but not yet
        // started is already in the set (TrackPersistence registers it synchronously before
        // dispatch), so awaiting the snapshot covers callbacks that haven't taken _persistLock yet —
        // the gap a bare `_persistLock.WaitAsync()/Release()` would miss. Loop because a draining
        // task could, in principle, queue another; in practice the scheduler is stopped first so the
        // second pass is empty.
        while (true)
        {
            Task[] pending;
            lock (_pendingPersistenceLock)
            {
                pending = [.. _pendingPersistence];
            }

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // Each tracked task already swallows + logs its own exceptions; WhenAll only
                // re-surfaces them. Draining must not throw — we only care that they finished.
            }
        }
    }

    /// <summary>
    /// Runs a fire-and-forget persistence callback while registering it in
    /// <see cref="_pendingPersistence"/> for the lifetime of the work, so
    /// <see cref="DrainPendingPersistenceAsync"/> can await it. The task is added synchronously on
    /// the caller's thread (before <see cref="Task.Run"/> schedules anything) and removed in a
    /// continuation when the body completes.
    /// </summary>
    private void TrackPersistence(Func<Task> body)
    {
        // Gate the work on a TCS so the task is registered in _pendingPersistence BEFORE its body
        // can run (and therefore before it can complete and try to remove itself). Without the gate,
        // a body that finished between Task.Run and the Add could remove-then-never-have-been-added,
        // or the Add could land after the removal continuation — either way leaving a stale entry or
        // missing one. Releasing the gate after the Add makes the add-then-remove order deterministic.
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task = Task.Run(async () =>
        {
            await gate.Task;
            await body();
        });

        lock (_pendingPersistenceLock)
        {
            _pendingPersistence.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_pendingPersistenceLock)
                {
                    _pendingPersistence.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        gate.SetResult();
    }

    /// <summary>
    /// Resumes all packages. Ensures every known package is registered with the scheduler
    /// first, so packages loaded in an Idle state (e.g. "Start Later") get picked up.
    /// </summary>
    public void StartPackages()
    {
        Package[] snapshot;
        lock (_lock)
        { snapshot = [.. Packages]; }

        DateTime now = DateTime.Now;
        foreach (Package package in snapshot)
        {
            // Respect scheduled-for-future packages: the toolbar's Start-all is a
            // bulk action. To override a schedule the user must explicitly right-click
            // the package and pick "Start now".
            if (package.ScheduledStartTime is { } scheduled && scheduled > now)
            {
                continue;
            }

            _scheduler.AddPackage(package);
        }

        _scheduler.StartAll();
    }

    /// <summary>
    /// Pauses or resumes all packages.
    /// </summary>
    /// <param name="resume">True to resume, false to pause.</param>
    public void PausePackages(bool resume)
    {
        if (resume)
        {
            _scheduler.StartAll();
        }
        else
        {
            _scheduler.PauseAll();
        }
    }

    /// <summary>
    /// Stops all packages.
    /// </summary>
    public void StopPackages() => _scheduler.StopAll();

    /// <summary>
    /// Removes a package or package file.
    /// </summary>
    /// <param name="item">The package or package file to remove.</param>
    public void RemovePackage(object item)
    {
        if (item is Package package)
        {
            _scheduler.RemovePackage(package);

            lock (_lock)
            {
                Packages.Remove(package);
            }

            // Soft-remove from Uploads only — keep the row so the Uploaded tab still
            // shows it. Removal from Uploaded is a separate per-file action that sets
            // each file's IsHidden flag.
            int? packageDbId = package.DbId;
            if (packageDbId is int pid)
            {
                TrackPersistence(async () =>
                {
                    await _persistLock.WaitAsync();
                    try
                    {
                        await _packageRepo.SoftRemoveFromUploadsAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(this, LogType.Error, $"Failed to soft-remove package from Uploads: {ex.Message}");
                    }
                    finally
                    {
                        _persistLock.Release();
                    }
                });
            }

            package.Remove();
        }
        else if (item is PackageFile packageFile)
        {
            packageFile.ForceStart = false;
            packageFile.Cts?.Cancel();
            packageFile.Cts?.Dispose();
            packageFile.Cts = null;

            int? fileDbId = packageFile.DbId;
            if (fileDbId is int fid)
            {
                TrackPersistence(async () =>
                {
                    await _persistLock.WaitAsync();
                    try
                    {
                        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { fid });
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(this, LogType.Error, $"Failed to soft-remove file from Uploads: {ex.Message}");
                    }
                    finally
                    {
                        _persistLock.Release();
                    }
                });
            }

            packageFile.Package.Remove(packageFile);
        }
    }

    /// <summary>
    /// Removes a single package file.
    /// </summary>
    /// <param name="packageFile">The package file to remove.</param>
    public static void RemovePackageFile(PackageFile packageFile)
    {
        packageFile.ForceStart = false;
        packageFile.Cts?.Cancel();
        packageFile.Cts?.Dispose();
        packageFile.Cts = null;
        packageFile.Package.Remove(packageFile);
    }

    /// <summary>
    /// Starts (or force-starts) a specific package or package file. Manual start
    /// overrides any pending scheduled start: <see cref="Package.ScheduledStartTime"/>
    /// is cleared and the package is registered with the scheduler immediately.
    /// Idle, Paused, Failed, and Cancelled files are all transitioned into the
    /// hash or upload queue; already-running and Completed files are left alone.
    /// </summary>
    /// <param name="item">The package or file to start.</param>
    public void StartPackage(object item)
    {
        if (IsPaused)
        {
            return;
        }

        if (item is Package package)
        {
            package.ScheduledStartTime = null;

            foreach (PackageFile file in package)
            {
                ForceQueueIfStartable(file);
            }

            // Idempotent: registers the package with the scheduler if it hasn't
            // been added yet (e.g. user clicked Start before a future-scheduled
            // delay elapsed). FillAvailableSlots — NOT StartAll — so only the files
            // we just queued above begin; other idle packages/files stay idle.
            _scheduler.AddPackage(package);
            _scheduler.FillAvailableSlots();
        }
        else if (item is PackageFile packageFile)
        {
            packageFile.Package.ScheduledStartTime = null;

            // scheduleIdleFiles:false — register the package WITHOUT auto-queuing its other
            // idle files. If the package wasn't registered yet (e.g. loaded with autostart
            // off), a plain AddPackage would SchedulePackageFiles → queue every idle file
            // in the package, so starting one row would start the whole package.
            _scheduler.AddPackage(packageFile.Package, scheduleIdleFiles: false);

            ForceQueueIfStartable(packageFile);
            // FillAvailableSlots so only this file starts — StartAll would requeue every
            // idle file across all packages (the bug where one row's Start ran everything).
            _scheduler.FillAvailableSlots();
        }
    }

    /// <summary>
    /// Force-starts a package or file: launches its upload past the UPLOAD admission gate (the
    /// global upload limit and the per-host limit) instead of queuing it to wait for a free
    /// upload slot like <see cref="StartPackage"/>. The hashing/CPU limit is still respected —
    /// a file that needs hashing waits for a free CPU slot first, then its upload jumps the
    /// limit. The launched upload is still counted by the scheduler when admitting normal files,
    /// so it over-fills a slot rather than raising the limit: once it (or another running upload)
    /// finishes and the running count drops back below the limit, normal admission resumes. Files
    /// already running or Completed are skipped by the scheduler. Unlike <see cref="StartPackage"/>,
    /// this is honoured even while the queue is globally paused (only the named files run).
    /// </summary>
    /// <param name="item">The package or file to force-start.</param>
    public void ForceStartPackage(object item)
    {
        if (item is Package package)
        {
            package.ScheduledStartTime = null;

            // Register the package (idempotently) so FillSlots counts its force-started files
            // when admitting normal uploads. scheduleIdleFiles:false — we hand the exact files
            // to ForceStart ourselves; don't let AddPackage sweep the package's other idle files
            // into the queue. ForceStart skips any file already running/completed.
            _scheduler.AddPackage(package, scheduleIdleFiles: false);
            _scheduler.ForceStart(package);
        }
        else if (item is PackageFile packageFile)
        {
            packageFile.Package.ScheduledStartTime = null;
            _scheduler.AddPackage(packageFile.Package, scheduleIdleFiles: false);
            _scheduler.ForceStart([packageFile]);
        }
    }

    /// <summary>
    /// Stops a specific package or package file.
    /// </summary>
    /// <param name="item">The package or file to stop.</param>
    public static void StopPackage(object item)
    {
        if (item is Package package)
        {
            foreach (PackageFile file in package)
            {
                StopFile(file);
            }
        }
        else if (item is PackageFile packageFile)
        {
            StopFile(packageFile);
        }
    }

    private static void ForceQueueIfStartable(PackageFile file)
    {
        // Don't disturb files that are already running or done. HashQueued/UploadQueued
        // also no-op: they're already in the queue; FillSlots will pick them up.
        if (file.State is FileState.Hashing
            or FileState.Uploading
            or FileState.HashQueued
            or FileState.UploadQueued
            or FileState.Completed)
        {
            return;
        }

        // No explicit RefreshConnection needed — AttemptRunner rebuilds the
        // HttpHandler at entry, so the retry naturally picks the next proxy.
        file.Error = null;

        // Re-queueing a TERMINAL file (manual Retry / per-row Start of a Failed or Cancelled
        // file) appends to the end: clear its stale QueueOrder so the subsequent
        // FillAvailableSlots → FillSlots → EnsureQueueOrdered (on the scheduler loop) gives it a
        // fresh position past the current max. Setting QueueOrder here is off-loop, consistent
        // with the file.State / file.Error mutations already done in this method. Idle is already
        // 0; Paused kept its place in the non-terminal set, so leave it to preserve its position.
        if (file.State is FileState.Failed or FileState.Cancelled)
        {
            file.QueueOrder = 0;
        }

        if (!file.IsHashingComplete || string.IsNullOrEmpty(file.FileHash))
        {
            file.State = FileState.HashQueued;
        }
        else
        {
            file.State = FileState.UploadQueued;
        }
    }

    /// <summary>
    /// Resets a package or file back to the start (re-hash + re-upload from scratch).
    /// </summary>
    public void ResetPackage(object item)
    {
        // ResetFile already transitions each file to HashQueued, so we only need to fill
        // slots — NOT StartAll, which would also requeue every idle/failed file in OTHER
        // packages (same over-reach bug as the per-row Start).
        if (item is Package package)
        {
            foreach (PackageFile file in package)
            {
                ResetFile(file);
            }

            _scheduler.AddPackage(package, scheduleIdleFiles: false);
            _scheduler.FillAvailableSlots();
        }
        else if (item is PackageFile packageFile)
        {
            ResetFile(packageFile);
            _scheduler.AddPackage(packageFile.Package, scheduleIdleFiles: false);
            _scheduler.FillAvailableSlots();
        }
    }

    private static void ResetFile(PackageFile file)
    {
        StopFile(file);
        // No explicit RefreshConnection — AttemptRunner rebuilds the HttpHandler against
        // the current rotation when the scheduler picks the file up again.
        file.IsHashingComplete = false;
        file.FileHash = null;         // clear stored hash so it is re-computed
        file.Error = null;
        file.FileUrl = null;
        file.IsUploadFinished = false;
        file.QueueOrder = 0;          // re-queue: append to the END of the upload order
        file.State = FileState.HashQueued;   // always restart from hash
    }

    private static void StopFile(PackageFile file)
    {
        // Clear the force-start override (also covers Reset, which routes through here): a hash
        // that finishes in the cancellation window must not let OnHashCompleted launch an
        // over-limit upload for a file the user just stopped/reset. See PackageFile.ForceStart.
        file.ForceStart = false;
        file.Cts?.Cancel();
        file.Cts?.Dispose();
        file.Cts = null;

        if (file.State is not FileState.Completed and not FileState.Idle)
        {
            file.State = FileState.Cancelled;
        }
    }
}
