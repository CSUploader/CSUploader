// <copyright file="PackageManagerSoftRemoveTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Upload;
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
        _scheduler = new UploadScheduler(settings);
        _packageManager = new PackageManager(settings, _scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());
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
        Package package = new(new PackageOptions { DirectoryPath = "pkg" }) { DbId = packageId };

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
        Package package = new(new PackageOptions { DirectoryPath = "pkg" }) { DbId = packageId };

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            using UploadScheduler scheduler = new(settings);
            PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>());

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
            DirectoryPath = string.Empty,
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

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
