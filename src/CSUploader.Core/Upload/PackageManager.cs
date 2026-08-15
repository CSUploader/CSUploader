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

    // Tail of the persistence chain: every write is sequenced onto this one, so they reach SQLite
    // in the order the scheduler produced them — and, being sequential, never overlap either. This
    // replaced a SemaphoreSlim, which gave the second guarantee without the first: when
    // PackageCompleted fires for the last file, "every prior file's state is already committed"
    // needs prior to actually mean prior. See TrackPersistence.
    private Task _persistTail = Task.CompletedTask;

    // Tracks every in-flight fire-and-forget persistence task so DrainPendingPersistenceAsync can
    // await ALL of them — including one that has been queued but hasn't started yet. Registering
    // happens synchronously on the event-firing thread BEFORE the work is dispatched, closing the
    // window where the drain would return while a queued callback was still pending, letting its
    // EF Core write race the SqliteConnection's dispose. See tests/CLAUDE.md.
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
    /// Raised after a file that had reached a terminal state is put back in the queue (Retry,
    /// Reset, or the re-upload of a completed file) and that has been persisted. The mirror of
    /// <see cref="FileCompleted"/> — the Uploaded tab lists rows the database calls Completed, so
    /// it needs to know when one stops being one.
    /// </summary>
    public event EventHandler<PackageFile>? FileReopened;

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
        Dictionary<string, FileHosterLoginDto> resolvedLogins = [with(StringComparer.Ordinal)];

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

            // A restored Completed row: the transferred-byte / progress counters aren't persisted (only State
            // is), so it would otherwise render an empty 0% progress bar, a blank "Bytes Loaded" and a full
            // "Bytes Remaining" (the ctor defaults BytesRemaining = Size). Reconstruct them from the known
            // outcome — a completed upload is 100%, fully sent, nothing left.
            if (pf.State == FileState.Completed)
            {
                pf.Progress = 100.0;
                pf.BytesLoaded = pf.Size;
                pf.BytesRemaining = 0;
            }

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
        // Snapshot the in-memory files up front — the SAME array orders the dtos, so the id
        // backfill below can pair them positionally.
        PackageFile[] files = [.. package];

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

            int sortOrder = 0;
            UploadPackageFileDto[] fileDtos = [.. files.Select(file => new UploadPackageFileDto
            {
                FileName = file.Name,
                FileDirectory = file.Path ?? string.Empty,
                FileSize = file.Size ?? 0,
                FileHoster = file.FileHoster.Name,
                FileHosterName = file.FileHoster.Name,
                // Denormalize the account label (DisplayName: username, or a masked API key for
                // key-only hosters; null for anonymous, which the History tab renders as the
                // localized "(anonymous)" via FileHosterLoginId==0). Storing DisplayName persists
                // the masked key (e.g. "12GHte**") for key-only accounts so History stays as
                // distinguishable as the live grid; only newly recorded rows get it.
                FileHosterAccount = file.FileHosterLogin is { IsAnonymous: false } login ? login.DisplayName : null,
                StartDateTime = DateTime.Now,
                State = file.State,
                IsHashingComplete = file.IsHashingComplete,
                FileHash = file.FileHash,
                FileHosterLoginId = file.FileHosterLogin?.Id ?? 0,
                SortOrder = sortOrder++,
                QueueOrder = file.QueueOrder,
            })];

            // One save for the whole graph. Inserted row by row, a failure partway through left a
            // package with only SOME of its file rows — and the files that missed out uploaded
            // with no DbId, so their every state change was silently unpersistable and they
            // vanished on restart. Now either the whole package survives a restart, or (on the
            // failure logged below) none of it pretends it will.
            await _packageRepo.InsertWithFilesAsync(pkgDto, fileDtos);

            package.DbId = pkgDto.Id;
            for (int i = 0; i < files.Length; i++)
            {
                files[i].DbId = fileDtos[i].Id;
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
        => PersistFileTransition(e.File, e.OldState, e.NewState);

    /// <summary>
    /// Writes one file transition to the database: the state itself, plus whatever else that
    /// transition implies — a newly computed hash, a discarded one, a package that is no longer
    /// complete — as a single sequenced write.
    /// </summary>
    /// <remarks>
    /// Separate from the event handler because a mutation can be worth persisting without changing
    /// the state: resetting a file that is ALREADY queued for hashing clears its hash and error but
    /// leaves it in <see cref="FileState.HashQueued"/>, and <c>SetFileState</c> raises nothing when
    /// the state does not move. Silently skipping the write there would leave the stale hash on
    /// disk — the precise failure this is all meant to close.
    /// </remarks>
    private void PersistFileTransition(PackageFile file, FileState oldState, FileState newState)
    {
        FileStateChangedEventArgs e = new(file, oldState, newState);

        if (e.File.DbId is null)
        {
            // No row yet, but the flag must still be consumed — otherwise it survives to be spent
            // on some later, unrelated transition.
            e.File.HashDiscarded = false;
            return;
        }

        FileState state = e.NewState;
        bool isTerminal = IsTerminal(state);

        // Read-and-clear inline: SetFileState raises this event synchronously on the pump, which is
        // also where the flag was set, so there is no window between the two.
        bool discardHash = e.File.HashDiscarded;
        e.File.HashDiscarded = false;

        // A file leaving a terminal state re-opens its package. Nothing ever wrote the completed
        // flag back to false, so retrying one file in a finished package left queued rows inside a
        // package still marked complete — and the Uploaded tab's export reads by that flag.
        bool reopened = IsTerminal(e.OldState) && !isTerminal;

        // Was this the package's last non-terminal file? Decided HERE, at event time on the pump —
        // the states are pump-owned, and the answer belongs to this transition. (It used to be read
        // at write time from the persistence thread: an unsynchronized scan whose answer could
        // reflect transitions that came after this one.)
        bool packageJustCompleted = isTerminal
            && e.File.Package.DbId is not null
            && e.File.Package.All(f => IsTerminal(f.State));

        FileTransitionWrite write = new()
        {
            FileId = e.File.DbId.Value,
            State = (int)state,
            Error = e.File.Error,
            FileUrl = e.File.FileUrl,
            FinishedDateTime = isTerminal ? (e.File.FinishedDate ?? DateTime.Now) : null,
            StartedDateTime = e.File.StartedDate,
            // Hashing → next-state transition is the natural "hash now valid" moment. Capture
            // the hash here (string is interned-cheap) and persist alongside the state change.
            HashToStore = e.OldState == FileState.Hashing
                    && e.File.IsHashingComplete
                    && !string.IsNullOrEmpty(e.File.FileHash)
                ? e.File.FileHash
                : null,
            DiscardHash = discardHash,
            PackageIdNoLongerCompleted = reopened ? e.File.Package.DbId : null,
            PackageIdNowCompleted = packageJustCompleted ? e.File.Package.DbId : null,
        };

        TrackPersistence(async () =>
        {
            FileTransitionResult result;
            try
            {
                result = await _fileRepo.PersistTransitionAsync(write);
            }
            catch (Exception ex)
            {
                // The transaction rolled back, so NOTHING below may fire: every event here
                // announces a fact as persisted, and none of them are.
                _logger.Log(this, LogType.Error, $"Failed to persist state: {ex.Message}");
                return;
            }

            if (!result.FileRowExisted)
            {
                // The row was already deleted — nothing was written, so nothing is announced.
                return;
            }

            if (reopened)
            {
                // The Uploaded tab lists rows the DB calls Completed. This one no longer is, so
                // it has to re-query — otherwise the row lingers there, contradicting the file
                // it claims to describe, until something else happens to refresh it.
                FileReopened?.Invoke(this, e.File);
            }

            if (isTerminal)
            {
                FileCompleted?.Invoke(this, e.File);

                // On the DATABASE's verdict, not packageJustCompleted's: memory believed the
                // package was done, but if an earlier file's write failed and rolled back, the
                // rows disagree — and announcing a completion the database doesn't show would
                // hand the export a package that is still missing work.
                if (result.PackageCompleted)
                {
                    PackageCompleted?.Invoke(this, e.File.Package);
                }
            }
        });
    }

    private static bool IsTerminal(FileState state)
        => state is FileState.Completed or FileState.Failed or FileState.Cancelled;

    private void OnQueueOrderChanged(object? sender, IReadOnlyList<PackageFile> files)
    {
        var orders = files
            .Where(f => f.DbId is not null)
            .ToDictionary(f => f.DbId!.Value, f => f.QueueOrder);
        if (orders.Count == 0)
        {
            return;
        }

        TrackPersistence(async () =>
        {
            try
            {
                await _fileRepo.UpdateQueueOrderAsync(orders);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to persist queue order: {ex.Message}");
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
        // Snapshot and await every tracked persistence task. A task that was queued but not yet
        // started is already in the set (TrackPersistence registers it synchronously before
        // dispatch), so awaiting the snapshot covers callbacks that have not begun. Loop because a
        // draining task could, in principle, queue another; in practice the scheduler is stopped
        // first so the second pass is empty.
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
    /// Queues a persistence callback behind the ones already queued, and registers it in
    /// <see cref="_pendingPersistence"/> for the lifetime of the work so
    /// <see cref="DrainPendingPersistenceAsync"/> can await it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CHAINED, not raced. Each body used to get its own <see cref="Task.Run"/> and then contend
    /// for a semaphore — which guarantees that no two writes overlap, but says nothing about which
    /// goes first. Two transitions produced microseconds apart could therefore
    /// reach the database backwards, and the scheduler produces exactly such pairs: a Stop followed
    /// by a Reset, or a reset's requeue and the hash launch that FillSlots does for it in the very
    /// same pump pass. Landing backwards leaves the row holding the earlier state permanently.
    /// Sequencing each write onto the previous one makes the database follow the order the
    /// scheduler actually produced.
    /// </para>
    /// <para>
    /// The chain must never break: a body that threw would fault the tail and, worse, surface in
    /// <see cref="DrainPendingPersistenceAsync"/>. Failures are logged and swallowed here so one bad
    /// write cannot stall or poison every write behind it.
    /// </para>
    /// </remarks>
    private void TrackPersistence(Func<Task> body)
    {
        Task task;
        lock (_pendingPersistenceLock)
        {
            task = _persistTail.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await body();
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(this, LogType.Error, $"Persistence step failed: {ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();

            _persistTail = task;

            // Registered here, under the same lock the removal continuation takes — so the entry is
            // always added before it can be removed, however fast the body finishes.
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
                    try
                    {
                        await _packageRepo.SoftRemoveFromUploadsAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(this, LogType.Error, $"Failed to soft-remove package from Uploads: {ex.Message}");
                    }
                });
            }

            package.Remove();
        }
        else if (item is PackageFile packageFile)
        {
            // The scheduler cancels it on its pump; this thread only takes the row out of the list.
            _scheduler.RemoveFile(packageFile);

            int? fileDbId = packageFile.DbId;
            if (fileDbId is int fid)
            {
                TrackPersistence(async () =>
                {
                    try
                    {
                        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { fid });
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(this, LogType.Error, $"Failed to soft-remove file from Uploads: {ex.Message}");
                    }
                });
            }

            packageFile.Package.Remove(packageFile);
        }
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

        // The queuing runs on the scheduler's pump, like Stop and Reset — ForceQueueIfStartable
        // writes file.State, and the pump is the one thread allowed to. Posting it BEFORE
        // AddPackage keeps the order the pump sees identical to the old inline order: queue the
        // startable files, then let AddPackage's SchedulePackageFiles sweep whatever is still idle,
        // then fill slots.
        if (item is Package package)
        {
            package.ScheduledStartTime = null;

            PackageFile[] files = [.. package];
            _scheduler.PostFileMutation(() =>
            {
                foreach (PackageFile file in files)
                {
                    ForceQueueIfStartable(file);
                }
            });

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

            _scheduler.PostFileMutation(() => ForceQueueIfStartable(packageFile));

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
    /// Renames a package (the Uploads tab's editable Name cell): updates the in-memory name — the
    /// setter raises PropertyChanged, so the grid cell and the name filter see it immediately — and
    /// persists it when the package has a DB row. The History tab joins package names off that row at
    /// load, so its groups pick the new name up on their next reload. Callers reject blank names;
    /// this trusts its input.
    /// </summary>
    public void RenamePackage(Package package, string newName)
    {
        package.Name = newName;

        if (package.DbId is int id)
        {
            // Through the chain like every other post-mutation write — a failed write must not take
            // down the rename (the in-memory name is already applied); it just won't survive a
            // restart, and the error lands in the log. On its own Task.Run this was the one write
            // that could both land out of order against a rapid second rename and slip past
            // DrainPendingPersistenceAsync entirely.
            TrackPersistence(() => _packageRepo.UpdateNameAsync(id, newName));
        }
    }

    /// <summary>
    /// Stops a specific package or package file.
    /// </summary>
    /// <remarks>
    /// Runs on the scheduler's pump rather than the calling thread — <see cref="StopFile"/> writes
    /// the same <c>Cts</c>/<c>State</c> the scheduler writes when it launches work, and the two
    /// interleaving is what left uploads running with no source to cancel them. Instance rather
    /// than static for that reason: it needs the scheduler to post to.
    /// </remarks>
    /// <param name="item">The package or file to stop.</param>
    public void StopPackage(object item)
    {
        if (item is Package package)
        {
            // Snapshot on this thread; the pump does the stopping.
            PackageFile[] files = [.. package];
            _scheduler.PostFileMutation(() =>
            {
                foreach (PackageFile file in files)
                {
                    StopFile(file);
                }
            });
        }
        else if (item is PackageFile packageFile)
        {
            _scheduler.PostFileMutation(() => StopFile(packageFile));
        }
    }

    private void ForceQueueIfStartable(PackageFile file)
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

        // A re-queued row must not keep showing the previous attempt's Elapsed/Started/Finished/
        // progress while it waits (the loader restores those from the persisted attempt across a
        // restart — a Failed file re-queued after relaunch otherwise sits in the queue displaying
        // the dead attempt's 3-hour Duration).
        file.ResetAttemptDisplay();

        // Re-queueing a TERMINAL file (manual Retry / per-row Start of a Failed or Cancelled
        // file) appends to the end: clear its stale QueueOrder so the subsequent
        // FillAvailableSlots → FillSlots → EnsureQueueOrdered gives it a fresh position past the
        // current max. This whole method runs ON the scheduler loop (StartPackage posts it), same
        // as the FillSlots that follows. Idle is already 0; Paused kept its place in the
        // non-terminal set, so leave it to preserve its position.
        if (file.State is FileState.Failed or FileState.Cancelled)
        {
            file.QueueOrder = 0;
        }

        // Announced, not assigned — the requeue (and the Error clear above it) has to reach the DB,
        // or a restart restores the terminal state the user just retried away from.
        _scheduler.ApplyFileState(
            file,
            !file.IsHashingComplete || string.IsNullOrEmpty(file.FileHash)
                ? FileState.HashQueued
                : FileState.UploadQueued);
    }

    /// <summary>
    /// Resets a package or file back to the start (re-hash + re-upload from scratch).
    /// </summary>
    public void ResetPackage(object item)
    {
        // ResetFile already transitions each file to HashQueued, so we only need to fill
        // slots — NOT StartAll, which would also requeue every idle/failed file in OTHER
        // packages (same over-reach bug as the per-row Start).
        // Registering the package first is safe — with scheduleIdleFiles:false it only records the
        // package, it does not read any file state. The reset and the slot-fill that follows are
        // both posted, and the pump is FIFO, so the files are back in the queue before FillSlots
        // looks at them. ResetFile writes Cts/State, so it belongs on the pump like Stop does.
        if (item is Package package)
        {
            PackageFile[] files = [.. package];
            _scheduler.AddPackage(package, scheduleIdleFiles: false);
            _scheduler.PostFileMutation(() =>
            {
                foreach (PackageFile file in files)
                {
                    ResetFile(file);
                }
            });
            _scheduler.FillAvailableSlots();
        }
        else if (item is PackageFile packageFile)
        {
            _scheduler.AddPackage(packageFile.Package, scheduleIdleFiles: false);
            _scheduler.PostFileMutation(() => ResetFile(packageFile));
            _scheduler.FillAvailableSlots();
        }
    }

    private void ResetFile(PackageFile file)
    {
        CancelWork(file);

        // No explicit RefreshConnection — AttemptRunner rebuilds the HttpHandler against
        // the current rotation when the scheduler picks the file up again.
        file.IsHashingComplete = false;
        file.FileHash = null;         // clear stored hash so it is re-computed
        file.HashDiscarded = true;    // ...and so the STORED one goes with it
        file.Error = null;
        file.FileUrl = null;
        file.IsUploadFinished = false;
        file.QueueOrder = 0;          // re-queue: append to the END of the upload order

        // Straight to HashQueued, never through Cancelled. The old code passed through it on the
        // way, which was invisible while these assignments were silent — now that the transition
        // is announced, stopping there would report the file as terminal (firing FileCompleted and
        // refreshing the Uploaded tab) in the middle of a reset.
        // One announced transition carries the whole reset to disk — the state, the cleared error,
        // and (via HashDiscarded) the cleared hash. Issuing the hash clear as its own write would
        // put it in a race with any hash write already queued for this file.
        FileState before = file.State;
        _scheduler.ApplyFileState(file, FileState.HashQueued); // always restart from hash

        if (before == FileState.HashQueued)
        {
            // Resetting a file that was ALREADY queued for hashing moves no state, so nothing was
            // announced — but the cleared hash and error still have to land.
            PersistFileTransition(file, before, FileState.HashQueued);
        }
    }

    private void StopFile(PackageFile file)
    {
        CancelWork(file);

        if (file.State is not FileState.Completed and not FileState.Idle)
        {
            _scheduler.ApplyFileState(file, FileState.Cancelled);
        }
    }

    /// <summary>
    /// Tears down whatever the file has in flight, without deciding what state it lands in — that
    /// differs between Stop (Cancelled) and Reset (straight back to HashQueued).
    /// </summary>
    private static void CancelWork(PackageFile file)
    {
        // Clear the force-start override: a hash that finishes in the cancellation window must not
        // let OnHashCompleted launch an over-limit upload for a file the user just stopped/reset.
        // See PackageFile.ForceStart.
        file.ForceStart = false;

        // The attempt being stopped no longer owns this row — a completion already queued behind
        // this stop must not be allowed to act on it. See PackageFile.SupersedeAttempt.
        file.SupersedeAttempt();

        // ONE read, not three. Read separately, a source installed between the Cancel and the
        // Dispose gets disposed without ever being cancelled — the shape that produced uploads
        // nothing could stop. Posting this onto the pump is what actually closes the window; this
        // makes the method safe to read on its own terms.
        if (file.Cts is CancellationTokenSource cts)
        {
            cts.Cancel();
            cts.Dispose();
        }

        file.Cts = null;
    }
}
