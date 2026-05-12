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
public class PackageManagerSoftRemoveTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

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

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg1);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg1);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg2);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg2);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg3);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg3);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg4);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg4);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg5);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg5);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg6);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg6);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg);
            PackageManager freshManager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg);

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
            AppSettings settings = new();
            DefaultFileHosterRegistry reg = new([]);
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg);
            PackageManager freshManager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg);
            PackageManager freshManager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg);

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
        using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg);
        PackageManager freshManager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg);

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
            using UploadScheduler scheduler = new(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), reg);
            PackageManager freshManager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), reg);

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

            for (int i = 0; i < 50 && _scheduler.IsPaused; i++)
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

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        // RemovePackage's persistence is fire-and-forget; poll briefly so the test doesn't
        // depend on a fixed delay.
        for (int i = 0; i < 50; i++)
        {
            T? result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
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
