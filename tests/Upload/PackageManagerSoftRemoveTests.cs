// <copyright file="PackageManagerSoftRemoveTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Verifies <see cref="PackageManager.RemovePackage"/> soft-deletes from the Uploads tab
/// instead of physically deleting rows: the package and its file rows must remain in the
/// database (for the Uploaded tab) with their <c>IsRemovedFromUploads</c> flag flipped,
/// and a subsequent <see cref="PackageManager.LoadPersistedPackagesAsync"/> must skip them.
/// </summary>
// Uses IAsyncLifetime rather than IDisposable so DisposeAsync can drain PackageManager's
// in-flight fire-and-forget persistence callbacks BEFORE closing the SqliteConnection.
// The callbacks (queued from OnFileStateChanged / RemovePackage as `_ = Task.Run(...)`)
// capture this instance's _fileRepo → _factory → _connection. If the connection closes
// while a callback is mid-write, the EF Core call throws, gets swallowed by the mocked
// IAppLogger, but congests the thread pool enough to time out the *next* test's
// WaitForAsync polling (50×50ms). Draining via PackageManager.DrainPendingPersistenceAsync
// (which takes + releases the same _persistLock those callbacks use) makes the teardown
// deterministic. See the dotnet-concurrency-specialist analysis for the full chain.
public class PackageManagerSoftRemoveTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly string _connectionString;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;
    private readonly RecordingLogger _logger = new();

    // Local manager+scheduler pairs created by individual tests (the "fresh restart" simulations).
    // Tracked so DisposeAsync can stop each scheduler and drain its fire-and-forget persistence —
    // since a reloaded file whose persisted QueueOrder is 0 makes the scheduler assign one and
    // write it back, and that write must not race the shared SQLite connection's dispose.
    private readonly List<(UploadScheduler Scheduler, PackageManager Manager)> _localPairs = [];

    public PackageManagerSoftRemoveTests()
    {
        // A NAMED shared-cache in-memory database addressed by connection STRING — not a single
        // shared SqliteConnection object.
        //
        // These tests observe a fire-and-forget write by polling for it, so a reader and a writer
        // are deliberately in flight at once. Pointing every DbContext at one SqliteConnection put
        // both on that one connection, which is not thread-safe: a measured probe of exactly this
        // shape failed 41 of 200 writes with "SQLite Error 5: unable to delete/modify user-function
        // due to active statements" — writes only, reads never. RemovePackage catches and logs that,
        // so the row silently never flipped and the poll burned its whole budget: the ~1-in-10
        // full-suite flake. Widening the timeout (50×50 ms → 100×100 ms) could never have fixed it.
        //
        // With a connection string each context opens its OWN connection into the shared cache and
        // SQLite does the locking. The same probe then reports 0 errors. Production was never
        // affected — it runs a file database where every context gets a pooled connection.
        _connectionString = $"Data Source=csu-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        string connectionString = _connectionString;
        _connection = new SqliteConnection(connectionString);
        _connection.Open(); // keeper: a shared-cache in-memory db lives only while one connection is open

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _packageRepo = new UploadPackageRepository(_factory);
        _fileRepo = new UploadPackageFileRepository(_factory);
        _loginRepo = new FileHosterLoginRepository(_factory);

        AppSettings settings = new();
        DefaultFileHosterRegistry registry = new([]);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        _packageManager = new PackageManager(settings, _scheduler, _packageRepo, _fileRepo, _loginRepo, _logger, registry);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Stop the source of new FileStateChanged events first — the scheduler's channel
        // consumer is what raises those, and disposing it drains the consumer loop.
        _scheduler.Dispose();

        // Same for every local manager+scheduler a test spun up against the shared connection.
        foreach ((UploadScheduler scheduler, _) in _localPairs)
        {
            scheduler.Dispose();
        }

        // Wait for any callback already past the FileStateChanged / QueueOrderChanged dispatch
        // (the `_ = Task.Run(...)` in OnFileStateChanged / OnQueueOrderChanged / RemovePackage) to
        // finish its EF Core write. Without this, the write races against the connection dispose
        // below and the failure leaks into the NEXT test as thread-pool congestion.
        await _packageManager.DrainPendingPersistenceAsync();
        foreach ((_, PackageManager manager) in _localPairs)
        {
            await manager.DrainPendingPersistenceAsync();
        }

        _connection.Dispose();
    }

    /// <summary>
    /// Builds a "fresh restart" manager+scheduler against the shared connection and tracks the pair
    /// so <see cref="DisposeAsync"/> drains its persistence. Use for tests that call
    /// <see cref="PackageManager.LoadPersistedPackagesAsync"/> and may schedule a package (which
    /// triggers a fire-and-forget queue-order write).
    /// </summary>
    private PackageManager NewLocalManager(AppSettings settings, DefaultFileHosterRegistry registry, out UploadScheduler scheduler)
    {
        scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);
        _localPairs.Add((scheduler, manager));
        return manager;
    }

    private PackageManager NewLocalManager(AppSettings settings, DefaultFileHosterRegistry registry)
        => NewLocalManager(settings, registry, out _);

    /// <summary>
    /// Settings whose scheduler admits no work at all — for tests that assert what an operation
    /// WROTE, without the queue running the file and overwriting it with the next attempt's result.
    /// </summary>
    private static AppSettings NoSlots() => new() { MaxConcurrentUploadJobs = 0, MaxConcurrentCPUJobs = 0 };

    [Fact]
    public async Task RemovePackage_PackageInstance_FlipsIsRemovedFromUploadsInDatabase()
    {
        int packageId = await InsertPackageAsync("pkg");
        Package package = new(new PackageOptions { Title = "pkg" }) { DbId = packageId };

        _packageManager.RemovePackage(package);

        UploadPackageDto? reloaded = await WaitForAsync(async () =>
        {
            UploadPackageDto? p = await _packageRepo.FindAsync(packageId);
            return p?.IsRemovedFromUploads == true ? p : null;
        });
        Assert.True(reloaded is not null, PersistenceDiagnostics("The package's soft-remove"));
        Assert.True(reloaded!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task RemovePackage_PackageInstance_LeavesPackageRowInDatabase()
    {
        // Soft-remove must NOT delete the row — the Uploaded tab still queries by file
        // and joins against the package name.
        int packageId = await InsertPackageAsync("pkg");
        Package package = new(new PackageOptions { Title = "pkg" }) { DbId = packageId };

        _packageManager.RemovePackage(package);

        UploadPackageDto? reloaded = await WaitForAsync(async () =>
        {
            UploadPackageDto? p = await _packageRepo.FindAsync(packageId);
            return p?.IsRemovedFromUploads == true ? p : null;
        });
        Assert.True(reloaded is not null, PersistenceDiagnostics("The package's soft-remove"));
        Assert.Equal("pkg", reloaded!.Name);
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_SkipsPackagesMarkedRemovedFromUploads()
    {
        // Simulate the post-restart load: a previously soft-removed package must NOT show
        // up in the Uploads tab again.
        int packageId = await InsertPackageAsync("pkg", isRemovedFromUploads: true);
        await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        await _packageManager.LoadPersistedPackagesAsync();

        Assert.DoesNotContain(_packageManager.Packages, p => p.DbId == packageId);
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_CompletedFile_RestoresFullProgressAndBytes()
    {
        // The transferred-byte / progress counters aren't persisted (only State is). A restored Completed
        // row must still read 100% with all bytes sent — not an empty 0% bar with blank "Bytes Loaded".
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            int pkgId = await InsertPackageAsync("done");           // default settings keep completed packages (Never)
            await InsertFileAtAsync(tempDir, pkgId, "a.iso", FileState.Completed);

            await _packageManager.LoadPersistedPackagesAsync();

            Package pkg = _packageManager.Packages.Single(p => p.DbId == pkgId);
            PackageFile file = Assert.Single(pkg);
            Assert.Equal(FileState.Completed, file.State);
            Assert.Equal(100.0, file.Progress);
            Assert.Equal(file.Size, file.BytesLoaded);
            Assert.Equal(0L, file.BytesRemaining);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AtStartupMode_SoftRemovesFullySuccessfulPackages()
    {
        // RemoveFinishedUploadsMode.AtStartup: a package whose every file completed
        // successfully should be flagged IsRemovedFromUploads=true on load, so the
        // Uploads tab starts the session clean. Persisted history on the Uploaded tab
        // is untouched (the row stays in the DB).
        // PackageFile's ctor reads FileInfo.Length so the test files have to exist on
        // disk; we use a temp dir cleaned up in finally.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // AutostartUploads=Never: the default OnlyIfRunningAtLastSession would schedule the
            // reloaded Uploading file (c.iso) → EnsureQueueOrdered → a fire-and-forget
            // UpdateQueueOrderAsync that opens an explicit transaction on the shared per-test
            // SqliteConnection, racing this test's own unsynchronized FindAsync reads below.
            AppSettings settings = new()
            {
                RemoveFinishedUploads = RemoveFinishedUploadsMode.AtStartup,
                AutostartUploads = AutostartUploadsMode.Never,
            };
            DefaultFileHosterRegistry reg1 = new([]);
            PackageManager manager = NewLocalManager(settings, reg1);

            int doneId = await InsertPackageAsync("done");
            await InsertFileAtAsync(tempDir, doneId, "a.iso", FileState.Completed);
            await InsertFileAtAsync(tempDir, doneId, "b.iso", FileState.Completed);

            int activeId = await InsertPackageAsync("active");
            await InsertFileAtAsync(tempDir, activeId, "c.iso", FileState.Uploading);

            await manager.LoadPersistedPackagesAsync();

            // Belt-and-braces: drain any in-flight fire-and-forget persistence so the DB reads
            // below can't observe a half-written row (or race the write's transaction).
            await manager.DrainPendingPersistenceAsync();

            UploadPackageDto? doneRow = await _packageRepo.FindAsync(doneId);
            UploadPackageDto? activeRow = await _packageRepo.FindAsync(activeId);
            Assert.NotNull(doneRow);
            Assert.True(doneRow!.IsRemovedFromUploads, "fully-completed package should be soft-removed");
            Assert.NotNull(activeRow);
            Assert.False(activeRow!.IsRemovedFromUploads, "in-flight package should stay visible");
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AutostartNever_DoesNotScheduleQueuedPackages()
    {
        // Mode Never: the package loads (visible on Uploads tab) but the scheduler
        // doesn't pick it up. The user has to click Start.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Never };
            DefaultFileHosterRegistry reg2 = new([]);
            PackageManager manager = NewLocalManager(settings, reg2, out UploadScheduler scheduler);

            int packageId = await InsertPackageAsync("queued");
            await InsertFileAtAsync(tempDir, packageId, "a.iso", FileState.UploadQueued);

            await manager.LoadPersistedPackagesAsync();

            Assert.Equal(0, scheduler.RegisteredPackageCount);
            Assert.Single(manager.Packages); // still loaded — just not scheduled
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AutostartAlways_SchedulesQueuedPackages()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Always };
            DefaultFileHosterRegistry reg3 = new([]);
            PackageManager manager = NewLocalManager(settings, reg3, out UploadScheduler scheduler);

            int packageId = await InsertPackageAsync("queued");
            await InsertFileAtAsync(tempDir, packageId, "a.iso", FileState.UploadQueued);

            await manager.LoadPersistedPackagesAsync();

            Assert.Equal(1, scheduler.RegisteredPackageCount);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_ForceAutostartNever_OverridesAlwaysMode_DoesNotSchedule()
    {
        // The Avalonia head's --agent guard. Even with the persisted policy set to Always — which
        // WOULD schedule a queued package on load (see _AutostartAlways_SchedulesQueuedPackages) —
        // a latched AppSettings makes AutostartUploads report Never, so LoadPersistedPackagesAsync
        // leaves the package unscheduled. Belt-and-braces vs. PauseAll: latching alone stops the
        // queuing here; PauseAll alone would leave files queued and one FillAvailableSlots/StartAll
        // away from really uploading, which is why the head applies both.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Always };
            settings.ForceAutostartUploadsNever(); // the --agent latch
            DefaultFileHosterRegistry reg = new([]);
            PackageManager manager = NewLocalManager(settings, reg, out UploadScheduler scheduler);

            int packageId = await InsertPackageAsync("queued");
            await InsertFileAtAsync(tempDir, packageId, "a.iso", FileState.UploadQueued);

            await manager.LoadPersistedPackagesAsync();

            Assert.Equal(0, scheduler.RegisteredPackageCount);
            Assert.Single(manager.Packages); // still loaded — just not scheduled
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AutostartOnlyIfRunning_ActiveState_SchedulesPackage()
    {
        // OnlyIfRunningAtLastSession + a file in an active state (Uploading) → schedule.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.OnlyIfRunningAtLastSession };
            DefaultFileHosterRegistry reg4 = new([]);
            PackageManager manager = NewLocalManager(settings, reg4, out UploadScheduler scheduler);

            int packageId = await InsertPackageAsync("running");
            // Pre-remap state Uploading counts as "was running at shutdown".
            await InsertFileAtAsync(tempDir, packageId, "a.iso", FileState.Uploading);

            await manager.LoadPersistedPackagesAsync();

            Assert.Equal(1, scheduler.RegisteredPackageCount);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AutostartOnlyIfRunning_PausedOnly_DoesNotSchedule()
    {
        // OnlyIfRunningAtLastSession + only Paused files → don't schedule. The user
        // explicitly paused, so we honour that on next launch.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.OnlyIfRunningAtLastSession };
            DefaultFileHosterRegistry reg5 = new([]);
            PackageManager manager = NewLocalManager(settings, reg5, out UploadScheduler scheduler);

            int packageId = await InsertPackageAsync("paused");
            await InsertFileAtAsync(tempDir, packageId, "a.iso", FileState.Paused);

            await manager.LoadPersistedPackagesAsync();

            Assert.Equal(0, scheduler.RegisteredPackageCount);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_NeverMode_LeavesAllPackagesVisible()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Never };
            DefaultFileHosterRegistry reg6 = new([]);
            PackageManager manager = NewLocalManager(settings, reg6);

            int doneId = await InsertPackageAsync("done");
            await InsertFileAtAsync(tempDir, doneId, "a.iso", FileState.Completed);

            await manager.LoadPersistedPackagesAsync();

            UploadPackageDto? doneRow = await _packageRepo.FindAsync(doneId);
            Assert.NotNull(doneRow);
            Assert.False(doneRow!.IsRemovedFromUploads);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    private async Task<int> InsertFileAtAsync(string dir, int packageId, string fileName, FileState state)
    {
        // Create the actual file so PackageFile's FileInfo.Length doesn't throw on load.
        string path = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(path, new byte[] { 0 });
        UploadPackageFileDto file = new()
        {
            FileName = fileName,
            FileDirectory = dir,
            FileSize = 1,
            FileHoster = "Rapidgator",
            FileHosterName = "Rapidgator",
            State = state,
            PackageId = packageId,
        };
        await _fileRepo.InsertAsync(file);
        return file.Id;
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_IncludesNonRemovedPackages()
    {
        // Counterpart to the skip test: confirm the filter is specific to the removed flag
        // (a package without files of any hoster the client knows about will still be skipped
        // for unrelated reasons, so we just assert here that the removal filter doesn't fire).
        int kept = await InsertPackageAsync("kept", isRemovedFromUploads: false);
        int removed = await InsertPackageAsync("removed", isRemovedFromUploads: true);

        await _packageManager.LoadPersistedPackagesAsync();

        // Neither will end up loaded because they have no files / no resolvable hoster, but
        // we can still assert via the DB that LoadPersistedPackagesAsync didn't *delete*
        // either — both rows are still present.
        Assert.NotNull(await _packageRepo.FindAsync(kept));
        Assert.NotNull(await _packageRepo.FindAsync(removed));
    }

    [Fact]
    public async Task StartPackage_ScheduledPackageNotYetRegistered_RegistersAndClearsSchedule()
    {
        // Repro: user added a package with Schedule mode and the delay hasn't elapsed.
        // Right-clicking → Start must override the schedule, register the package with
        // the scheduler, and queue any Idle files.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "scheduled",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            _packageManager.ScheduleDelayedStart(package, DateTime.Now.AddHours(1));
            PackageFile file = package.Single();
            Assert.Equal(FileState.Idle, file.State);
            Assert.Equal(0, _scheduler.RegisteredPackageCount);
            Assert.NotNull(package.ScheduledStartTime);

            _packageManager.StartPackage(package);
            await _scheduler.DrainAsync(); // the queuing runs on the pump now, not on this thread

            Assert.Null(package.ScheduledStartTime);
            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            // Could be HashQueued/UploadQueued or already advanced to Hashing/Uploading
            // by the scheduler's background loop — any non-Idle state proves we kicked it off.
            Assert.NotEqual(FileState.Idle, file.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task AddLaterWithSavedAccountThenReload_PackageReappears()
    {
        // Same as AddLaterThenReload but the FileHosterLogin actually exists in the
        // accounts table — i.e. the user picked a saved account in the wizard. This
        // is the path real users hit (the login lookup at LoadPersistedPackagesAsync
        // line 209 returns a real row instead of null).
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            // Persist a hoster login first.
            FileHosterLoginDto savedLogin = new()
            {
                FileHosterName = "Rapidgator",
                Username = "user",
                Password = "pw",
            };
            await _loginRepo.InsertAsync(savedLogin);
            Assert.True(savedLogin.Id > 0);

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "later",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, savedLogin } },
            };

            Package added = await _packageManager.AddPackageOnlyAsync(options);

            AppSettings settings = new();
            DefaultFileHosterRegistry reg = new([]);
            // NewLocalManager tracks the pair so DisposeAsync drains the fire-and-forget queue-order
            // write that reloading a QueueOrder-0 file triggers (would otherwise race conn dispose).
            PackageManager freshManager = NewLocalManager(settings, reg);

            await freshManager.LoadPersistedPackagesAsync();

            Assert.Single(freshManager.Packages);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task AddLaterThenReload_PackageReappears()
    {
        // End-to-end repro: drive the wizard's "Add later" path via AddPackageOnlyAsync,
        // then create a fresh PackageManager pointing at the same DB and call
        // LoadPersistedPackagesAsync. The reload should produce the same package.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "later",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package added = await _packageManager.AddPackageOnlyAsync(options);
            Assert.NotNull(added.DbId);
            int packageId = added.DbId!.Value;

            // Fresh manager+scheduler against the same shared SQLite — simulates a restart.
            // NewLocalManager tracks the pair so DisposeAsync drains the fire-and-forget queue-order
            // write that reloading a QueueOrder-0 file triggers (would otherwise race conn dispose).
            AppSettings settings = new();
            DefaultFileHosterRegistry reg = new([]);
            PackageManager freshManager = NewLocalManager(settings, reg);

            await freshManager.LoadPersistedPackagesAsync();

            Assert.Single(freshManager.Packages);
            Package loaded = freshManager.Packages.Single();
            Assert.Equal(packageId, loaded.DbId);
            Assert.Single(loaded);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_OneBadPackage_DoesNotAbortOthers()
    {
        // Repro: a Completed package whose source file has been deleted threw
        // FileNotFoundException out of FileInfo.Length inside PackageFile.ctor,
        // and the exception escaped the per-package loop — wiping every package
        // remaining in iteration order from the Uploads tab on restart.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string goodPath = Path.Combine(tempDir, "good.iso");
            await File.WriteAllBytesAsync(goodPath, new byte[] { 0 });
            string ghostPath = Path.Combine(tempDir, "ghost.iso");

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);

            // Persist a "ghost" package whose Completed file points at a path that
            // no longer exists on disk. Insert it FIRST so it's iterated first —
            // before the bug fix this would tank the load for every later package.
            UploadPackageDto ghostPkg = new()
            {
                Name = "ghost",
                CreatedDateTime = DateTime.Now,
                IsCompleted = true,
            };
            await _packageRepo.InsertAsync(ghostPkg);
            await _fileRepo.InsertAsync(new UploadPackageFileDto
            {
                FileName = "ghost.iso",
                FileDirectory = tempDir,
                FileSize = 1,
                FileHoster = "Rapidgator",
                FileHosterName = "Rapidgator",
                FileHosterLoginId = 0,
                State = FileState.Completed,
                PackageId = ghostPkg.Id,
            });

            // Then a normal package whose file is on disk.
            PackageOptions options = new()
            {
                Title = "good",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [goodPath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            await _packageManager.AddPackageOnlyAsync(options);

            AppSettings settings = new();
            DefaultFileHosterRegistry reg = new([]);
            // Tracked pair so DisposeAsync drains the fire-and-forget queue-order write a reloaded
            // QueueOrder-0 file triggers (would otherwise race the shared connection's dispose).
            PackageManager freshManager = NewLocalManager(settings, reg);

            await freshManager.LoadPersistedPackagesAsync();

            // The ghost package's terminal-state row keeps it visible (no disk read
            // needed for display); the good package must also load.
            Assert.Contains(freshManager.Packages, p => p.Name == "good");
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_NonTerminalFileWithMissingSource_StillLoadsThePackage()
    {
        // We deliberately don't check File.Exists at load time. The runtime path
        // (HashingService / HttpHandler.UploadFileAsync) will surface "file not found"
        // as a real Failed state once the user actually starts the upload — duplicating
        // that check at load time just spammed Errors for files the user hadn't acted on.
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        UploadPackageDto pkg = new() { Name = "ghosted", CreatedDateTime = DateTime.Now };
        await _packageRepo.InsertAsync(pkg);
        await _fileRepo.InsertAsync(new UploadPackageFileDto
        {
            FileName = "part1.rar",
            FileDirectory = @"C:\path\that\does\not\exist",
            FileSize = 1024,
            FileHoster = "Rapidgator",
            FileHosterName = "Rapidgator",
            State = FileState.UploadQueued,
            PackageId = pkg.Id,
        });

        AppSettings settings = new();
        DefaultFileHosterRegistry reg = new([]);
        // Tracked pair so DisposeAsync drains the fire-and-forget queue-order write a reloaded
        // QueueOrder-0 file triggers (would otherwise race the shared connection's dispose).
        PackageManager freshManager = NewLocalManager(settings, reg);

        await freshManager.LoadPersistedPackagesAsync();

        Package loaded = Assert.Single(freshManager.Packages);
        Assert.Single(loaded);
        // DB row stays visible — the user can decide whether to Remove or Retry it.
        UploadPackageDto? reloaded = (await _packageRepo.GetAllAsync()).FirstOrDefault(p => p.Id == pkg.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_RestoresStartedDateFinishedDateAndDuration()
    {
        // Regression: the loader used to leave StartedDate / FinishedDate / Duration
        // unset on the in-memory PackageFile, so Completed files reloaded from a
        // previous session showed a blank "Finished" column even though the DB had
        // the timestamp.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            DateTime started = new(2026, 5, 12, 19, 17, 10, DateTimeKind.Local);
            DateTime finished = new(2026, 5, 12, 19, 17, 25, DateTimeKind.Local);

            UploadPackageDto pkg = new() { Name = "completed", CreatedDateTime = DateTime.Now, IsCompleted = true };
            await _packageRepo.InsertAsync(pkg);
            await _fileRepo.InsertAsync(new UploadPackageFileDto
            {
                FileName = "a.iso",
                FileDirectory = tempDir,
                FileSize = 1,
                FileHoster = "Rapidgator",
                FileHosterName = "Rapidgator",
                State = FileState.Completed,
                PackageId = pkg.Id,
                StartDateTime = started,
                FinishedDateTime = finished,
            });

            AppSettings settings = new();
            DefaultFileHosterRegistry reg = new([]);
            // Tracked pair so DisposeAsync drains the fire-and-forget queue-order write a reloaded
            // QueueOrder-0 file triggers (would otherwise race the shared connection's dispose).
            PackageManager freshManager = NewLocalManager(settings, reg);

            await freshManager.LoadPersistedPackagesAsync();

            Package loaded = Assert.Single(freshManager.Packages);
            PackageFile file = loaded.Single();
            Assert.Equal(started, file.StartedDate);
            Assert.Equal(finished, file.FinishedDate);
            Assert.Equal(finished - started, file.Duration);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task LoadPersistedPackagesAsync_AnonymousFile_ReconstitutesAnonymousCredential()
    {
        // An anonymous upload (the wizard's built-in Anonymous option) persists with
        // FileHosterLoginId=0 — there's no account row. On reload the credential must come
        // back flagged IsAnonymous so the pipeline takes its no-login path; otherwise an
        // anonymous package reloaded after a restart fails with "no API key / no username".
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            UploadPackageDto pkg = new() { Name = "anon", CreatedDateTime = DateTime.Now };
            await _packageRepo.InsertAsync(pkg);
            await _fileRepo.InsertAsync(new UploadPackageFileDto
            {
                FileName = "a.iso",
                FileDirectory = tempDir,
                FileSize = 1,
                FileHoster = "Hexload",
                FileHosterName = "Hexload",
                FileHosterLoginId = 0,           // the anonymous sentinel
                State = FileState.UploadQueued,
                PackageId = pkg.Id,
            });

            // Never mode keeps the scheduler from starting the reloaded package — we only
            // care that the credential was reconstituted correctly.
            AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Never };
            DefaultFileHosterRegistry reg = new([]);
            // Tracked pair so DisposeAsync drains the fire-and-forget queue-order write a reloaded
            // QueueOrder-0 file triggers (would otherwise race the shared connection's dispose).
            PackageManager freshManager = NewLocalManager(settings, reg);

            await freshManager.LoadPersistedPackagesAsync();

            Package loaded = Assert.Single(freshManager.Packages);
            PackageFile file = loaded.Single();
            Assert.NotNull(file.FileHosterLogin);
            Assert.True(file.FileHosterLogin.IsAnonymous);
            Assert.Equal("Hexload", file.FileHosterLogin.FileHosterName);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResetPackage_ReregistersAndStartsScheduler()
    {
        // Repro: after a Failed file is right-click → Reset, the file transitioned to
        // HashQueued but the scheduler never picked it up because nothing called
        // StartAll afterwards (and AddPackage is idempotent — no work for an already-
        // registered package).
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "p",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            PackageFile file = package.Single();
            // Simulate a stopped/failed state — the scenario the user reported.
            file.State = FileState.Failed;
            file.Error = "boom";
            _scheduler.PauseAll();

            _packageManager.ResetPackage(file);

            // Reset posts the file mutation and then the slot-fill onto the scheduler's pump, so
            // one drain covers both: it cannot complete until every action queued ahead of it has
            // run. IsPaused going false is what proves the slot-fill was actually queued and
            // processed — the original bug was that nothing resumed the scheduler at all, leaving
            // the reset file sitting in the queue forever.
            await _scheduler.DrainAsync();

            Assert.Null(file.Error);
            Assert.NotEqual(FileState.Failed, file.State);
            Assert.False(_scheduler.IsPaused);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StartPackage_SingleFile_LeavesOtherPackagesIdleFilesUntouched()
    {
        // Regression: the Uploads tab's per-row "Start" called StartPackage → StartAll →
        // RequeueStartableFiles, which swept EVERY idle file across all packages into the
        // queue. So right-clicking one row and choosing Start ran everything. The fix uses
        // FillAvailableSlots (no requeue) + register-without-scheduling, so only the picked
        // file starts and other packages' idle files stay idle.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package pkg1 = await CreateIdlePackageAsync(tempDir, "p1", "a.iso");
            Package pkg2 = await CreateIdlePackageAsync(tempDir, "p2", "b.iso");
            PackageFile fileA = pkg1.Single();
            PackageFile fileB = pkg2.Single();

            // Register pkg2 with the scheduler WITHOUT queuing its idle file, so it's in the
            // scheduler's package set (the precondition that let the old RequeueStartableFiles
            // sweep fileB) while fileB stays Idle.
            _scheduler.AddPackage(pkg2, scheduleIdleFiles: false);

            Assert.Equal(FileState.Idle, fileA.State);
            Assert.Equal(FileState.Idle, fileB.State);

            // Start only fileA.
            _packageManager.StartPackage(fileA);

            // Poll until fileA leaves Idle — proves the async FillSlots actually ran.
            for (int i = 0; i < 250 && fileA.State == FileState.Idle; i++)
            {
                await Task.Delay(20);
            }
            Assert.NotEqual(FileState.Idle, fileA.State);

            // The OTHER package's idle file must NOT have been swept into the queue.
            Assert.Equal(FileState.Idle, fileB.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StopPackage_PersistsTheCancelledState_SoARestartDoesNotResumeIt()
    {
        // The per-row Stop used to assign file.State directly, which raised no FileStateChanged and
        // so never reached the DB. The row stayed Uploading on disk, and the loader's default
        // OnlyIfRunningAtLastSession policy reads that as "was running when we closed" — so a
        // stopped upload came back and resumed on the next launch.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;

            // Put the row where a running upload leaves it, on disk as well as in memory.
            file.State = FileState.Uploading;
            await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Uploading, null, null);

            _packageManager.StopPackage(file);
            await _scheduler.DrainAsync();
            await _packageManager.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.Equal(FileState.Cancelled, persisted?.State);

            // ...and go the whole way: a fresh manager loading that row must leave the file alone.
            // Uploading is what the loader re-queues; Cancelled is terminal and stays put.
            PackageManager restarted = NewLocalManager(
                new AppSettings { AutostartUploads = AutostartUploadsMode.OnlyIfRunningAtLastSession },
                new DefaultFileHosterRegistry([]),
                out UploadScheduler restartedScheduler);
            await restarted.LoadPersistedPackagesAsync();
            await restartedScheduler.DrainAsync();
            await restarted.DrainPendingPersistenceAsync();

            PackageFile reloaded = restarted.Packages.SelectMany(p => p).Single(f => f.DbId == fileId);
            Assert.Equal(FileState.Cancelled, reloaded.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResetPackage_ClearsThePersistedHash_SoARestartReallyDoesRehash()
    {
        // Reset cleared the hash in memory only. Nothing in the persistence path ever CLEARS a
        // hash — it only writes one — so the row kept the old hash and its IsHashingComplete flag.
        // Reset a file, close before it gets a slot, and it came back hashed and went straight to
        // uploading: the one thing Reset exists to prevent.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;

            file.FileHash = "deadbeef";
            file.IsHashingComplete = true;
            await _fileRepo.UpdateHashAsync(fileId, "deadbeef");

            // A manager whose scheduler admits nothing, so the reset is observed as it lands rather
            // than after the queue has already re-hashed the file and written a fresh hash over it.
            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.ResetPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.True(string.IsNullOrEmpty(persisted?.FileHash), $"the stale hash survived the reset: {persisted?.FileHash}");
            Assert.False(persisted?.IsHashingComplete);
            Assert.Equal(FileState.HashQueued, persisted?.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StartPackage_PersistsTheRequeuedState_SoARetryIsNotForgotten()
    {
        // Retrying a Failed row has to reach the DB too, or a restart restores the failure the user
        // just retried away from.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;

            file.State = FileState.Failed;
            file.Error = "boom";
            await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Failed, "boom", null);

            // Admits nothing, so what is asserted is the requeue itself — not the outcome of the
            // attempt the queue would otherwise go on to run (and, with no real hoster, fail).
            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.StartPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.NotEqual(FileState.Failed, persisted?.State);
            Assert.True(string.IsNullOrEmpty(persisted?.Error), $"the old error survived the retry: {persisted?.Error}");
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StopThenReset_PersistsInThatOrder_SoTheResetIsNotOverwrittenByTheStop()
    {
        // Two user actions a moment apart queue two writes. Each used to get its own Task.Run and
        // then contend for a semaphore, which prevents overlap but not overtaking — so the Stop's
        // Cancelled could reach SQLite after the Reset's HashQueued and leave the row stopped.
        // A guard rather than a reproduction: with the writes chained the order is structural, and
        // this fails the moment anything makes them independent again.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;
            file.State = FileState.Uploading;

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.StopPackage(file);
            idle.ResetPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.Equal(FileState.HashQueued, persisted?.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResettingAnAlreadyQueuedFile_StillClearsThePersistedHash()
    {
        // Reset lands the file on HashQueued. If it was already there, the state does not move and
        // SetFileState announces nothing — so the write that carries the cleared hash never
        // happened, and the file came back hashed after a restart. The one case where a mutation
        // is worth persisting without a state change.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;

            file.State = FileState.HashQueued;
            file.FileHash = "deadbeef";
            file.IsHashingComplete = true;
            await _fileRepo.UpdateHashAsync(fileId, "deadbeef");
            await _fileRepo.UpdateStateAsync(fileId, (int)FileState.HashQueued, null, null);

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.ResetPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.True(string.IsNullOrEmpty(persisted?.FileHash), $"the stale hash survived the reset: {persisted?.FileHash}");
            Assert.False(persisted?.IsHashingComplete);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResettingAFinishedPackage_ClearsItsPersistedCompletedFlag()
    {
        // Nothing ever wrote IsCompleted back to false, so retrying one file left queued rows
        // inside a package the DB still called complete — and the Uploaded tab's export reads by
        // exactly that flag, so an in-progress package could be exported as finished.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Completed;
            await _packageRepo.UpdateCompletedFlagAsync(package.DbId!.Value, true);

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.ResetPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageDto? persisted = await _packageRepo.FindAsync(package.DbId!.Value);
            Assert.False(persisted?.IsCompleted, "the package is running again — it is not completed");
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResettingACompletedFile_AnnouncesThatItLeftTheDoneList()
    {
        // The Uploaded tab lists rows the DB calls Completed and refreshes on FileCompleted. Once
        // Reset started persisting, a reset row stopped being Completed on disk while still sitting
        // in that grid, with no event to tell it otherwise.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Completed;

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);
            List<PackageFile> reopened = [];
            idle.FileReopened += (_, f) => reopened.Add(f);

            idle.ResetPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            Assert.Equal([file], reopened);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ForceStartingACompletedFile_ClearsThePersistedHashToo()
    {
        // The re-upload path discards the hash for the same reason Reset does — the file on disk
        // may have changed — and had the same gap: it only cleared the in-memory copy.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;
            file.State = FileState.Completed;
            file.FileHash = "deadbeef";
            file.IsHashingComplete = true;
            await _fileRepo.UpdateHashAsync(fileId, "deadbeef");

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            idle.ForceStartPackage(file);
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.True(string.IsNullOrEmpty(persisted?.FileHash), $"the stale hash survived the re-upload: {persisted?.FileHash}");
            Assert.False(persisted?.IsHashingComplete);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenTheTransitionWriteFails_NoEventFires_AndTheRowIsUntouched()
    {
        // The persistence contract: every event announces a fact as PERSISTED. When the
        // transaction rolls back, none of them are facts — a FileCompleted fired anyway would let
        // RemoveFinishedUploads=Immediately prune a row whose database state still says Uploading.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;
            file.State = FileState.Uploading;
            await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Uploading, null, null);

            // A manager whose FILE repository fails every write, over the same database.
            DbContextOptions<CSUploaderDbContext> faultingOptions = new DbContextOptionsBuilder<CSUploaderDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(new FaultingCommandInterceptor("\"UploadPackageFile\""))
                .Options;
            UploadScheduler scheduler = new(NoSlots(), BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), new DefaultFileHosterRegistry([]));
            PackageManager manager = new(
                NoSlots(), scheduler, _packageRepo,
                new UploadPackageFileRepository(new TestDbContextFactory(faultingOptions)),
                _loginRepo, _logger, new DefaultFileHosterRegistry([]));
            _localPairs.Add((scheduler, manager));

            List<string> fired = [];
            manager.FileCompleted += (_, _) => fired.Add("FileCompleted");
            manager.FileReopened += (_, _) => fired.Add("FileReopened");
            manager.PackageCompleted += (_, _) => fired.Add("PackageCompleted");

            manager.StopPackage(file);
            await scheduler.DrainAsync();
            await manager.DrainPendingPersistenceAsync();

            Assert.Empty(fired);
            Assert.Contains(_logger.Messages, m => m.Contains("Failed to persist state", StringComparison.Ordinal));

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.Equal(FileState.Uploading, persisted?.State); // the row kept its pre-stop shape
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task HashCompletion_PersistsTheHash_OnTheEverydayNonTerminalTransition()
    {
        // Hashing → UploadQueued is how every routine pre-upload hash lands in the DB — the ONLY
        // route, since nothing else in production writes a hash. A restart must find it, or
        // hash-before-upload hosters re-hash multi-GB files every session.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            int fileId = file.DbId!.Value;

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);

            // The shape OnHashCompleted leaves behind: hash computed and valid, transitioning off
            // Hashing. Driven through ApplyFileState so it takes the real persistence path.
            file.State = FileState.Hashing;
            file.FileHash = "cafebabe";
            file.IsHashingComplete = true;
            scheduler.PostFileMutation(() => scheduler.ApplyFileState(file, FileState.UploadQueued));
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            UploadPackageFileDto? persisted = await _fileRepo.FindAsync(fileId);
            Assert.Equal("cafebabe", persisted?.FileHash);
            Assert.True(persisted?.IsHashingComplete);
            Assert.Equal(FileState.UploadQueued, persisted?.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task PackageCompleted_DoesNotFire_WhenTheDbStillHoldsANonTerminalSibling()
    {
        // Memory can believe the package is done while the database disagrees — a sibling's
        // transition failed and rolled back earlier in the chain, so its row is still running.
        // Announcing completion then would export a "finished" package that is missing work; the
        // database is the arbiter, and it declines.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string pathA = Path.Combine(tempDir, "a.iso");
            string pathB = Path.Combine(tempDir, "b.iso");
            await File.WriteAllBytesAsync(pathA, new byte[] { 0 });
            await File.WriteAllBytesAsync(pathB, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "p",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [pathA, pathB],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = await _packageManager.AddPackageOnlyAsync(options);
            PackageFile fileA = package.First();
            PackageFile fileB = package.Skip(1).First();

            // Simulate fileA's failed write: MEMORY says terminal, its ROW still says Uploading.
            fileA.State = FileState.Failed;
            await _fileRepo.UpdateStateAsync(fileA.DbId!.Value, (int)FileState.Uploading, null, null);

            PackageManager idle = NewLocalManager(NoSlots(), new DefaultFileHosterRegistry([]), out UploadScheduler scheduler);

            // The reviving manager must OWN the package — Start/Reset/ForceStart decline items
            // outside their manager's Packages (see PackageManager.IsAlive). In production the one
            // manager both created and revives; these tests split those roles to pin settings.
            idle.Packages.Add(package);
            List<Package> completed = [];
            idle.PackageCompleted += (_, p) => completed.Add(p);

            fileB.State = FileState.Uploading;
            scheduler.PostFileMutation(() => scheduler.ApplyFileState(fileB, FileState.Completed));
            await scheduler.DrainAsync();
            await idle.DrainPendingPersistenceAsync();

            Assert.Empty(completed);
            UploadPackageDto? persisted = await _packageRepo.FindAsync(package.DbId!.Value);
            Assert.False(persisted?.IsCompleted);

            // FileB's own completion DID land — only the package-level claim was declined.
            UploadPackageFileDto? b = await _fileRepo.FindAsync(fileB.DbId!.Value);
            Assert.Equal(FileState.Completed, b?.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("force")]
    [InlineData("reset")]
    public async Task RevivingADetachedFile_DoesNothing(string via)
    {
        // A row removed from the grid while a confirmation dialog was open is a ghost: its file
        // left its package. Reviving it re-registered the package with the scheduler (a phantom
        // empty row for the UI) and mutated a file nothing would ever schedule. All three revival
        // entry points decline dead items instead.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Completed;

            // The auto-remove path: the file leaves its package, the empty package leaves the manager.
            _packageManager.RemovePackage(file);
            _packageManager.RemovePackage(package);
            await _scheduler.DrainAsync();
            Assert.Equal(0, _scheduler.RegisteredPackageCount);

            switch (via)
            {
                case "start": _packageManager.StartPackage(file); break;
                case "force": _packageManager.ForceStartPackage(file); break;
                default: _packageManager.ResetPackage(file); break;
            }

            await _scheduler.DrainAsync();
            await _packageManager.DrainPendingPersistenceAsync();

            Assert.Equal(0, _scheduler.RegisteredPackageCount); // no resurrection
            Assert.Equal(FileState.Completed, file.State);      // the ghost was left alone
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task RevivingAnUnmanagedPackage_DoesNotReRegisterIt()
    {
        // The package flavour: reset a package row whose package was already auto-removed. The
        // scheduler must not learn about it again.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            Assert.Contains(package, _packageManager.Packages); // genuinely managed first

            _packageManager.RemovePackage(package);
            await _scheduler.DrainAsync();
            Assert.Equal(0, _scheduler.RegisteredPackageCount);

            _packageManager.ResetPackage(package);
            _packageManager.StartPackage(package);
            _packageManager.ForceStartPackage(package);
            await _scheduler.DrainAsync();

            Assert.Equal(0, _scheduler.RegisteredPackageCount);

            // A file still INSIDE a package this manager never owned is just as dead — the file
            // overload must not let the unmanaged package in through the side door.
            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
            Package foreign = new(new PackageOptions
            {
                Title = "foreign",
                Logger = Mock.Of<IAppLogger>(),
                Settings = new AppSettings(),
                FileHosters = new() { { hoster, login } },
            });
            string path = Path.Combine(tempDir, "foreign.iso");
            await File.WriteAllBytesAsync(path, new byte[] { 0 });
            PackageFile orphan = new(foreign, path, hoster, login);
            foreign.AddPackageFiles([orphan]);
            orphan.State = FileState.Failed;

            _packageManager.StartPackage(orphan);
            _packageManager.ResetPackage(orphan);
            _packageManager.ForceStartPackage(orphan);
            await _scheduler.DrainAsync();

            Assert.Equal(0, _scheduler.RegisteredPackageCount);
            Assert.Equal(FileState.Failed, orphan.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Fails any command whose SQL touches the given quoted table name — the same fault-injection
    /// shape as the Dal repository tests, here to prove the MANAGER's reaction to a failed write:
    /// no events, and the row left in its pre-transition shape.
    /// </summary>
    private sealed class FaultingCommandInterceptor(string failCommandsTouching)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(failCommandsTouching, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"injected fault: statement touches {failCommandsTouching}");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<Package> CreateIdlePackageAsync(string tempDir, string title, string fileName)
    {
        string filePath = Path.Combine(tempDir, fileName);
        await File.WriteAllBytesAsync(filePath, new byte[] { 0 });
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = title,
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = [filePath],
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        return await _packageManager.AddPackageOnlyAsync(options);
    }

    [Fact]
    public async Task StartPackages_SkipsFutureScheduledPackages()
    {
        // Toolbar Start-all should not start packages whose scheduled time hasn't elapsed.
        // The user has to right-click → "Start now" to override.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "scheduled",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            _packageManager.ScheduleDelayedStart(package, DateTime.Now.AddHours(1));
            Assert.Equal(0, _scheduler.RegisteredPackageCount);

            _packageManager.StartPackages();

            Assert.Equal(0, _scheduler.RegisteredPackageCount);
            Assert.NotNull(package.ScheduledStartTime);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StartPackages_StartsPackagesWithPastSchedule()
    {
        // A package whose scheduled time has already elapsed should be picked up by
        // Start-all — its schedule has effectively expired.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "expired",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            // Past schedule — ScheduleDelayedStart adds immediately when delay <= 0,
            // but for this assertion we keep the in-memory ScheduledStartTime set so
            // StartPackages still has to make the >Now decision.
            package.ScheduledStartTime = DateTime.Now.AddMinutes(-5);

            _packageManager.StartPackages();

            Assert.Equal(1, _scheduler.RegisteredPackageCount);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StartPackage_IndividualIdleFile_RegistersPackageAndTransitionsFile()
    {
        // Right-click a single file (not the whole package) → Start. The package should
        // get registered with the scheduler, only the chosen file should be queued, and
        // its parent's pending schedule should be cleared.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string fileA = Path.Combine(tempDir, "a.iso");
            string fileB = Path.Combine(tempDir, "b.iso");
            await File.WriteAllBytesAsync(fileA, new byte[] { 0 });
            await File.WriteAllBytesAsync(fileB, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "scheduled",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [fileA, fileB],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            _packageManager.ScheduleDelayedStart(package, DateTime.Now.AddHours(1));
            PackageFile target = package.First();

            _packageManager.StartPackage(target);

            // ForceQueueIfStartable runs on the scheduler's pump (it writes file.State), so drain
            // before reading the target's state. Sibling files are NOT asserted: with
            // scheduleIdleFiles:false nothing sweeps them, which is the point of the per-row Start.
            await _scheduler.DrainAsync();

            Assert.Null(package.ScheduledStartTime);
            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            Assert.NotEqual(FileState.Idle, target.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ForceStartPackage_ScheduledPackage_ClearsScheduleRegistersAndStartsFile()
    {
        // Force start on a future-scheduled package must override the schedule, register the
        // package, and kick the file off — like Start now, but past the concurrency gate.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "scheduled",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            _packageManager.ScheduleDelayedStart(package, DateTime.Now.AddHours(1));
            PackageFile file = package.Single();
            Assert.Equal(FileState.Idle, file.State);
            Assert.Equal(0, _scheduler.RegisteredPackageCount);

            _packageManager.ForceStartPackage(package);

            Assert.Null(package.ScheduledStartTime);
            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            for (int i = 0; i < 250 && file.State == FileState.Idle; i++)
            {
                await Task.Delay(20);
            }

            Assert.NotEqual(FileState.Idle, file.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ForceStartPackage_WhileGloballyPaused_StillStartsFile()
    {
        // The discriminator vs. StartPackage: a global pause makes StartPackage a no-op (the
        // file stays Idle), but ForceStartPackage launches the file anyway and leaves the pause
        // in place for everything else.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.iso");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "paused",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            PackageFile file = package.Single();

            _scheduler.PauseAll();
            for (int i = 0; i < 250 && !_packageManager.IsPaused; i++)
            {
                await Task.Delay(20);
            }

            Assert.True(_packageManager.IsPaused);

            _packageManager.ForceStartPackage(file);

            for (int i = 0; i < 250 && file.State == FileState.Idle; i++)
            {
                await Task.Delay(20);
            }

            Assert.NotEqual(FileState.Idle, file.State);
            Assert.True(_packageManager.IsPaused); // force start must not lift the global pause
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ForceStartPackage_SingleFile_LeavesSiblingIdle()
    {
        // Force-starting one file must register the package without sweeping its other idle
        // files into the queue (same surgical contract as the per-row Start).
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string fileA = Path.Combine(tempDir, "a.iso");
            string fileB = Path.Combine(tempDir, "b.iso");
            await File.WriteAllBytesAsync(fileA, new byte[] { 0 });
            await File.WriteAllBytesAsync(fileB, new byte[] { 0 });

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "two",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [fileA, fileB],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };

            Package package = await _packageManager.AddPackageOnlyAsync(options);
            PackageFile target = package.First();
            PackageFile sibling = package.Last();

            _packageManager.ForceStartPackage(target);

            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            for (int i = 0; i < 250 && target.State == FileState.Idle; i++)
            {
                await Task.Delay(20);
            }

            Assert.NotEqual(FileState.Idle, target.State);
            Assert.Equal(FileState.Idle, sibling.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StopPackage_ForceStartedFile_ClearsForceStartFlag()
    {
        // Closes the Stop-during-force-hash race: a force-started file that is stopped must have
        // ForceStart cleared, so if its in-flight hash completes the scheduler does NOT take the
        // force branch and launch an over-limit upload for the stopped file.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Hashing; // simulate a force-started file mid-hash
            file.ForceStart = true;

            _packageManager.StopPackage(file);
            await _scheduler.DrainAsync(); // the stop runs on the pump now, not on this thread

            Assert.False(file.ForceStart);
            Assert.Equal(FileState.Cancelled, file.State);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ResetPackage_ForceStartedFile_ClearsForceStartFlag()
    {
        // Reset routes through StopFile, which must clear ForceStart — otherwise a hash that
        // finishes in the window would launch an upload with the just-cleared hash, over the limit.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Hashing;
            file.ForceStart = true;

            _packageManager.ResetPackage(file);
            await _scheduler.DrainAsync(); // the reset runs on the pump now, not on this thread

            Assert.False(file.ForceStart);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task RemovePackage_ForceStartedFile_ClearsForceStartFlag()
    {
        // Removing a force-started file must clear ForceStart so a hash completing after removal
        // can't launch a detached, over-limit upload.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Package package = await CreateIdlePackageAsync(tempDir, "p", "a.iso");
            PackageFile file = package.Single();
            file.State = FileState.Hashing;
            file.ForceStart = true;

            _packageManager.RemovePackage(file);
            await _scheduler.DrainAsync(); // the detach runs on the pump now, not on this thread

            Assert.False(file.ForceStart);
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Records what <see cref="PackageManager"/> logs. Its persistence callbacks CATCH every
    /// exception and log it ("Failed to soft-remove package from Uploads: …"), so with a
    /// <c>Mock.Of&lt;IAppLogger&gt;()</c> a write that dies takes its own diagnosis with it and the
    /// test can only report a bare <c>Assert.NotNull() Failure</c> ten seconds later. Feeding the
    /// captured lines into the assertion turns the next flake into evidence instead of a mystery.
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<string> _messages = [];

        public event LogEventHandler? OnLogOutput;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public void Log(
            object? sender,
            LogType logType,
            string text,
            HttpTransaction? httpTransaction = null,
            string filePath = "",
            string function = "",
            int lineNumber = 0)
        {
            lock (_messages)
            {
                _messages.Add($"[{logType}] {text}");
            }

            _ = OnLogOutput; // nothing subscribes in these tests; recording is the whole job
        }
    }

    /// <summary>Assertion message carrying whatever the manager logged — see <see cref="RecordingLogger"/>.</summary>
    private string PersistenceDiagnostics(string what)
        => _logger.Messages.Count == 0
            ? $"{what} never landed, and the manager logged nothing at all."
            : $"{what} never landed. The manager logged:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", _logger.Messages);

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        // RemovePackage's persistence is fire-and-forget; poll until it commits. The loop returns the
        // instant the probe succeeds, so the happy path is unaffected and the budget only bounds how
        // long a genuine failure takes to surface.
        //
        // The budget is NOT what made this reliable, and the note that used to live here — blaming
        // thread-pool congestion delaying a correct-but-slow write — was wrong. The flake was a write
        // that never happened at all: the fixture shared one SqliteConnection between this poll and
        // the writer, which is not thread-safe, so the write threw and RemovePackage swallowed it.
        // See the connection-string comment in the constructor. Widening this loop (it was already
        // taken from 50×50 ms to 100×100 ms once) could never have helped.
        for (int i = 0; i < 100; i++)
        {
            T? result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private async Task<int> InsertPackageAsync(string name, bool isRemovedFromUploads = false)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = false,
            IsRemovedFromUploads = isRemovedFromUploads,
        };
        await _packageRepo.InsertAsync(pkg);
        return pkg.Id;
    }

    private async Task<int> InsertFileAsync(int packageId, string fileName, FileState state)
    {
        UploadPackageFileDto file = new()
        {
            FileName = fileName,
            FileDirectory = "C:\\test",
            FileSize = 1024,
            FileHoster = "Rapidgator",
            FileHosterName = "Rapidgator",
            State = state,
            PackageId = packageId,
        };
        await _fileRepo.InsertAsync(file);
        return file.Id;
    }

    private static AttemptRunner BuildAttemptRunner()
    {
        DefaultFileHosterRegistry registry = new([]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
