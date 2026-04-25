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
    private readonly UploadScheduler _scheduler;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly IAppLogger _logger;
    private readonly Lock _lock = new();

    // Serializes state-change persistence so that when PackageCompleted fires for the last file,
    // every prior file's UpdateStateAsync (and its URL) has already been committed to SQLite.
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageManager"/> class.
    /// </summary>
    /// <param name="settings">The application settings.</param>
    /// <param name="scheduler">The upload scheduler.</param>
    /// <param name="packageRepo">The upload package repository.</param>
    /// <param name="fileRepo">The upload package file repository.</param>
    /// <param name="loginRepo">The file hoster login repository.</param>
    /// <param name="logger">The application logger.</param>
    public PackageManager(
        AppSettings settings,
        UploadScheduler scheduler,
        UploadPackageRepository packageRepo,
        UploadPackageFileRepository fileRepo,
        FileHosterLoginRepository loginRepo,
        IAppLogger logger)
    {
        _scheduler = scheduler;
        _packageRepo = packageRepo;
        _fileRepo = fileRepo;
        _loginRepo = loginRepo;
        _logger = logger;

        _scheduler.PackageAdded += (_, package) => PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));
        _scheduler.FileStateChanged += OnFileStateChanged;
        _scheduler.Start();
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
    public void SchedulePackage(Package package)
    {
        _scheduler.AddPackage(package);
    }

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
    /// Loads incomplete packages from the database and resumes them.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task LoadPersistedPackagesAsync()
    {
        try
        {
            UploadPackageDto[] incomplete = await _packageRepo.GetIncompleteAsync();

            foreach (UploadPackageDto pkgDto in incomplete)
            {
                if (pkgDto.Files is null || pkgDto.Files.Count == 0)
                {
                    continue;
                }

                // Build FileHosterLogins dictionary
                Dictionary<FileHosterClient, FileHosterLoginDto> fileHosterLogins = [];
                Dictionary<string, SharedSession> sessions = new(StringComparer.Ordinal);

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

                    FileHosterLoginDto? login = fileDto.FileHosterLoginId > 0
                        ? await _loginRepo.FindAsync(fileDto.FileHosterLoginId)
                        : null;
                    login ??= new FileHosterLoginDto { FileHosterName = hosterName };

                    fileHosterLogins[client] = login;
                    sessions[hosterName] = new SharedSession();
                }

                if (fileHosterLogins.Count == 0)
                {
                    continue;
                }

                // Reconstruct Package
                PackageOptions options = new()
                {
                    DirectoryPath = pkgDto.DirectoryPath,
                    Logger = _logger,
                    FileHosters = fileHosterLogins,
                };
                Package package = new(options)
                {
                    DbId = pkgDto.Id,
                    ScheduledStartTime = pkgDto.ScheduledStartTime,
                    SpeedLimitKBps = pkgDto.SpeedLimitKBps,
                };

                // Override Name since it was persisted
                package.Name = pkgDto.Name ?? package.Name;

                // Reconstruct PackageFiles
                List<PackageFile> files = [];
                foreach (UploadPackageFileDto fileDto in pkgDto.Files)
                {
                    string hosterName = fileDto.FileHosterName ?? fileDto.FileHoster ?? string.Empty;
                    var client = FileHosterClient.FindByHost(hosterName, Protocol.Http, _logger);
                    if (client is null)
                    {
                        continue;
                    }

                    FileHosterLoginDto? login = fileDto.FileHosterLoginId > 0
                        ? await _loginRepo.FindAsync(fileDto.FileHosterLoginId)
                        : null;
                    login ??= new FileHosterLoginDto { FileHosterName = hosterName };

                    if (sessions.TryGetValue(hosterName, out SharedSession? session))
                    {
                        client.SharedSessionCache = session;
                    }

                    string filePath = Path.Combine(fileDto.FileDirectory ?? string.Empty, fileDto.FileName ?? string.Empty);

                    // Only add if file still exists on disk
                    if (!File.Exists(filePath))
                    {
                        _logger.Log(this, LogType.Error, $"File no longer exists: {filePath}");
                        continue;
                    }

                    PackageFile pf = new(package, filePath, client, login)
                    {
                        DbId = fileDto.Id,
                        Priority = fileDto.Priority,
                        IsHashingComplete = fileDto.IsHashingComplete
                    };
                    client.SpeedLimitProvider = pf.GetEffectiveSpeedLimitBytesPerSecond;

                    // Remap state: interrupted Hashing/Uploading -> re-queue
                    FileState restoredState = fileDto.State switch
                    {
                        FileState.Hashing => FileState.HashQueued,
                        FileState.Uploading => FileState.UploadQueued,
                        FileState.Idle when pf.RequiresHashingBeforeUpload && !pf.IsHashingComplete => FileState.HashQueued,
                        FileState.Idle => FileState.UploadQueued,
                        _ => fileDto.State,
                    };
                    pf.State = restoredState;
                    pf.Error = fileDto.Error;
                    pf.FileUrl = fileDto.FileUrl;

                    files.Add(pf);
                }

                if (files.Count == 0)
                {
                    continue;
                }

                // If every file is already in a terminal state, mark the package complete in DB
                // and skip adding it to the active list. This backfills packages that were left
                // with IsCompleted=false under stricter earlier logic.
                if (files.TrueForAll(f => f.State is FileState.Completed or FileState.Failed or FileState.Cancelled))
                {
                    await _packageRepo.UpdateCompletedFlagAsync(pkgDto.Id, true);
                    continue;
                }

                package.AddPackageFiles([.. files]);

                lock (_lock)
                { Packages.Add(package); }
                PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));

                // Resume scheduling for packages that should auto-start
                bool hasQueuedFiles = files.Any(f => f.State is FileState.HashQueued or FileState.UploadQueued);
                if (hasQueuedFiles)
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
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to load persisted packages: {ex.Message}");
        }
    }

    private async Task<Package> CreatePackageAsync(PackageOptions options)
    {
        Package package = new(options);

        if (!string.IsNullOrEmpty(package.SaveFrom))
        {
            package.AddPackageFiles(package.SaveFrom);
        }

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
                DirectoryPath = package.SaveFrom ?? string.Empty,
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
                    StartDateTime = DateTime.Now,
                    State = file.State,
                    IsHashingComplete = file.IsHashingComplete,
                    FileHosterLoginId = file.FileHosterLogin?.Id ?? 0,
                    Priority = file.Priority,
                    SortOrder = sortOrder++,
                    PackageId = package.DbId.Value,
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
        DateTime? finishedDateTime = state is FileState.Completed or FileState.Failed or FileState.Cancelled
            ? (e.File.FinishedDate ?? DateTime.Now)
            : null;

        _ = Task.Run(async () =>
        {
            await _persistLock.WaitAsync();
            try
            {
                await _fileRepo.UpdateStateAsync(fileId, (int)state, error, fileUrl, finishedDateTime);

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

    /// <summary>
    /// Resumes all packages. Ensures every known package is registered with the scheduler
    /// first, so packages loaded in an Idle state (e.g. "Start Later") get picked up.
    /// </summary>
    public void StartPackages()
    {
        Package[] snapshot;
        lock (_lock)
        { snapshot = [.. Packages]; }
        foreach (Package package in snapshot)
        {
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

            package.Remove();
        }
        else if (item is PackageFile packageFile)
        {
            packageFile.Cts?.Cancel();
            packageFile.Cts?.Dispose();
            packageFile.Cts = null;
            packageFile.Package.Remove(packageFile);
        }
    }

    /// <summary>
    /// Removes a single package file.
    /// </summary>
    /// <param name="packageFile">The package file to remove.</param>
    public static void RemovePackageFile(PackageFile packageFile)
    {
        packageFile.Cts?.Cancel();
        packageFile.Cts?.Dispose();
        packageFile.Cts = null;
        packageFile.Package.Remove(packageFile);
    }

    /// <summary>
    /// Starts (retries) a specific package or package file.
    /// </summary>
    /// <param name="item">The package or file to retry.</param>
    public void StartPackage(object item)
    {
        if (IsPaused)
        {
            return;
        }

        if (item is Package package)
        {
            // Re-queue failed/cancelled files
            foreach (PackageFile file in package)
            {
                RetryFileIfNeeded(file);
            }

            _scheduler.StartAll();
        }
        else if (item is PackageFile packageFile)
        {
            RetryFileIfNeeded(packageFile);
            _scheduler.StartAll();
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

    private static void RetryFileIfNeeded(PackageFile file)
    {
        if (file.State is FileState.Failed or FileState.Cancelled)
        {
            if (file.RequiresHashingBeforeUpload && !file.IsHashingComplete)
            {
                file.State = FileState.HashQueued;
            }
            else
            {
                file.State = FileState.UploadQueued;
            }
        }
    }

    /// <summary>
    /// Resets a package or file back to the start (re-hash + re-upload from scratch).
    /// </summary>
    public void ResetPackage(object item)
    {
        if (item is Package package)
        {
            foreach (PackageFile file in package)
            {
                ResetFile(file);
            }

            _scheduler.AddPackage(package);
        }
        else if (item is PackageFile packageFile)
        {
            ResetFile(packageFile);
            _scheduler.AddPackage(packageFile.Package);
        }
    }

    private static void ResetFile(PackageFile file)
    {
        StopFile(file);
        file.IsHashingComplete = false;
        file.Error = null;
        file.FileUrl = null;
        file.IsUploadFinished = false;
        file.State = file.RequiresHashingBeforeUpload ? FileState.HashQueued : FileState.UploadQueued;
    }

    private static void StopFile(PackageFile file)
    {
        file.Cts?.Cancel();
        file.Cts?.Dispose();
        file.Cts = null;

        if (file.State is not FileState.Completed and not FileState.Idle)
        {
            file.State = FileState.Cancelled;
        }
    }
}
