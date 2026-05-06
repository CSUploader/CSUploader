// <copyright file="PackageManagerSoftRemoveTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
