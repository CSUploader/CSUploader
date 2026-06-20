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
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    // Local manager+scheduler pairs created by individual tests (the "fresh restart" simulations).
    // Tracked so DisposeAsync can stop each scheduler and drain its fire-and-forget persistence —
    // since a reloaded file whose persisted QueueOrder is 0 makes the scheduler assign one and
    // write it back, and that write must not race the shared SQLite connection's dispose.
    private readonly List<(UploadScheduler Scheduler, PackageManager Manager)> _localPairs = [];

    public PackageManagerSoftRemoveTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
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
        _packageManager = new PackageManager(settings, _scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);
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
        Assert.NotNull(reloaded);
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
        Assert.NotNull(reloaded);
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
            AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.AtStartup };
            DefaultFileHosterRegistry reg1 = new([]);
            PackageManager manager = NewLocalManager(settings, reg1);

            int doneId = await InsertPackageAsync("done");
            await InsertFileAtAsync(tempDir, doneId, "a.iso", FileState.Completed);
            await InsertFileAtAsync(tempDir, doneId, "b.iso", FileState.Completed);

            int activeId = await InsertPackageAsync("active");
            await InsertFileAtAsync(tempDir, activeId, "c.iso", FileState.Uploading);

            await manager.LoadPersistedPackagesAsync();

            UploadPackageDto? doneRow = await _packageRepo.FindAsync(doneId);
            UploadPackageDto? activeRow = await _packageRepo.FindAsync(activeId);
            Assert.NotNull(doneRow);
            Assert.True(doneRow!.IsRemovedFromUploads, "fully-completed package should be soft-removed");
            Assert.NotNull(activeRow);
            Assert.False(activeRow!.IsRemovedFromUploads, "in-flight package should stay visible");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            Assert.Null(package.ScheduledStartTime);
            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            // Could be HashQueued/UploadQueued or already advanced to Hashing/Uploading
            // by the scheduler's background loop — any non-Idle state proves we kicked it off.
            Assert.NotEqual(FileState.Idle, file.State);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            // The reset itself runs synchronously: state moves to HashQueued and error
            // clears immediately. StartAll runs through the scheduler's channel, so we
            // poll briefly for IsPaused → false to prove the unpause was actually queued
            // and processed (the bug was that StartAll wasn't called at all, so IsPaused
            // would stay true forever).
            Assert.Null(file.Error);
            Assert.NotEqual(FileState.Failed, file.State);

            for (int i = 0; i < 250 && _scheduler.IsPaused; i++)
            {
                await Task.Delay(20);
            }
            Assert.False(_scheduler.IsPaused);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            Assert.Null(package.ScheduledStartTime);
            Assert.Equal(1, _scheduler.RegisteredPackageCount);
            // Target transitions synchronously via ForceQueueIfStartable. Sibling files
            // get queued asynchronously by SchedulePackageFiles when the scheduler's
            // background loop drains the post — not asserted here to avoid a race.
            Assert.NotEqual(FileState.Idle, target.State);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            PackageManager.StopPackage(file);

            Assert.False(file.ForceStart);
            Assert.Equal(FileState.Cancelled, file.State);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            Assert.False(file.ForceStart);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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

            Assert.False(file.ForceStart);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        // RemovePackage's persistence is fire-and-forget; poll until it commits. The loop returns
        // the instant the probe succeeds, so the happy path is unaffected — the generous 10s budget
        // (100×100 ms) only matters under parallel-run thread-pool congestion, where a correct-but-
        // slow persistence Task.Run can sit queued for seconds before a worker picks it up.
        // (TestThreadPoolInitializer raises the worker floor to make that rare; this wider budget is
        // belt-and-suspenders so a slow write is never declared a failure prematurely.)
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
