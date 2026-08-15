// <copyright file="UploadPackageRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class UploadPackageRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;

    public UploadPackageRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _packageRepo = new UploadPackageRepository(_factory);
        _fileRepo = new UploadPackageFileRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_FlipsFlagOnPackageWithoutDeleting()
    {
        int packageId = await InsertPackageAsync("pkg");

        await _packageRepo.SoftRemoveFromUploadsAsync(packageId);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(packageId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_CascadesToOwnedFiles()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId1 = await InsertFileAsync(packageId, "a.iso", FileState.Completed);
        int fileId2 = await InsertFileAsync(packageId, "b.iso", FileState.Uploading);

        await _packageRepo.SoftRemoveFromUploadsAsync(packageId);

        UploadPackageFileDto? f1 = await _fileRepo.FindAsync(fileId1);
        UploadPackageFileDto? f2 = await _fileRepo.FindAsync(fileId2);
        Assert.True(f1!.IsRemovedFromUploads);
        Assert.True(f2!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_WhenTheFileSweepFails_RollsBackThePackageFlag()
    {
        // The package flag is written first, the file sweep second. If the sweep fails, the flag
        // must not survive alone — a package the Uploads tab has dropped whose files the loader
        // still restores as live rows. The transaction is what makes this hold; as two
        // autocommitted statements (the old shape) the flag lands and this test fails.
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        DbContextOptions<CSUploaderDbContext> faultingOptions = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FaultingCommandInterceptor("\"UploadPackageFile\""))
            .Options;
        UploadPackageRepository faulting = new(new TestDbContextFactory(faultingOptions));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulting.SoftRemoveFromUploadsAsync(packageId));

        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.False(package!.IsRemovedFromUploads);
        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.False(file!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task InsertWithFilesAsync_InsertsTheWholeGraph_AndBackfillsTheIds()
    {
        UploadPackageDto pkg = new() { Name = "pkg", CreatedDateTime = DateTime.Now };
        UploadPackageFileDto[] files =
        [
            new() { FileName = "a.iso", FileDirectory = "C:\\t", FileHoster = "Rapidgator", FileHosterName = "Rapidgator" },
            new() { FileName = "b.iso", FileDirectory = "C:\\t", FileHoster = "Rapidgator", FileHosterName = "Rapidgator" },
        ];

        await _packageRepo.InsertWithFilesAsync(pkg, files);

        Assert.True(pkg.Id > 0);
        Assert.All(files, f => Assert.True(f.Id > 0));
        Assert.All(files, f => Assert.Equal(pkg.Id, f.PackageId));
        Assert.NotEqual(files[0].Id, files[1].Id);

        UploadPackageFileDto? a = await _fileRepo.FindAsync(files[0].Id);
        Assert.Equal("a.iso", a!.FileName);
        Assert.Equal(pkg.Id, a.PackageId);
    }

    [Fact]
    public async Task InsertWithFilesAsync_WhenAFileInsertFails_ThePackageRowDoesNotSurviveAlone()
    {
        // The old shape — package insert, then one autocommitted insert per file — could die
        // partway and leave a package with only some of its rows; the files that missed out had no
        // DbId, uploaded unpersistably, and vanished on restart. All-or-nothing now: a failing
        // file insert takes the package row with it.
        UploadPackageDto pkg = new() { Name = "doomed", CreatedDateTime = DateTime.Now };
        UploadPackageFileDto[] files =
        [
            new() { FileName = "a.iso", FileDirectory = "C:\\t", FileHoster = "Rapidgator", FileHosterName = "Rapidgator" },
        ];

        DbContextOptions<CSUploaderDbContext> faultingOptions = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FaultingCommandInterceptor("\"UploadPackageFile\""))
            .Options;
        UploadPackageRepository faulting = new(new TestDbContextFactory(faultingOptions));

        // SaveChanges wraps the interceptor's exception in a DbUpdateException; the precise type
        // is EF's business — what matters is that nothing survived.
        await Assert.ThrowsAnyAsync<Exception>(() => faulting.InsertWithFilesAsync(pkg, files));

        UploadPackageDto[] all = await _packageRepo.GetAllAsync();
        Assert.DoesNotContain(all, p => p.Name == "doomed");
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_WhenThePackageFlagFails_TheFileSweepNeverLands()
    {
        // Complement of the test above, faulting the FIRST-executed statement instead of the
        // second. Trivially green in today's order; under a statement reorder it becomes the
        // effective transaction proof, so the pair covers both orders.
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        DbContextOptions<CSUploaderDbContext> faultingOptions = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FaultingCommandInterceptor("\"UploadPackage\""))
            .Options;
        UploadPackageRepository faulting = new(new TestDbContextFactory(faultingOptions));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulting.SoftRemoveFromUploadsAsync(packageId));

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.False(file!.IsRemovedFromUploads);
        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.False(package!.IsRemovedFromUploads);
    }

    /// <summary>
    /// Fails any command whose SQL touches the given quoted table name — fault injection for
    /// proving a multi-statement write is genuinely transactional. Matching includes the
    /// identifier quotes because "UploadPackage" is a prefix of "UploadPackageFile".
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
            ThrowIfMatch(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        // Inserts don't go through the non-query path: SaveChanges reads the generated ids back,
        // so its commands execute as readers. Without this override the insert tests fault nothing.
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // Only fault WRITES that reach the table — the assert phase SELECTs from it too.
            if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                ThrowIfMatch(command);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfMatch(System.Data.Common.DbCommand command)
        {
            if (command.CommandText.Contains(failCommandsTouching, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"injected fault: statement touches {failCommandsTouching}");
            }
        }
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_DoesNotTouchOtherPackagesOrTheirFiles()
    {
        int targetId = await InsertPackageAsync("target");
        int otherId = await InsertPackageAsync("other");
        int otherFileId = await InsertFileAsync(otherId, "leave-alone.iso", FileState.Completed);

        await _packageRepo.SoftRemoveFromUploadsAsync(targetId);

        UploadPackageDto? otherPkg = await _packageRepo.FindAsync(otherId);
        UploadPackageFileDto? otherFile = await _fileRepo.FindAsync(otherFileId);
        Assert.False(otherPkg!.IsRemovedFromUploads);
        Assert.False(otherFile!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_LeavesIsHiddenAlone()
    {
        // The two soft-delete flags are independent — Uploads-side removal must not
        // accidentally hide the file from the Uploaded tab and vice-versa.
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        await _packageRepo.SoftRemoveFromUploadsAsync(packageId);

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.False(file!.IsHidden);
    }

    [Fact]
    public async Task UpdateCompletedFlagAsync_FlipsFlagWithoutTouchingOtherFields()
    {
        int packageId = await InsertPackageAsync("pkg");

        await _packageRepo.UpdateCompletedFlagAsync(packageId, true);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(packageId);
        Assert.True(reloaded!.IsCompleted);
        Assert.Equal("pkg", reloaded.Name);
    }

    [Fact]
    public async Task UpdateCompletedFlagAsync_CanFlipBackToFalse()
    {
        int packageId = await InsertPackageAsync("pkg", isCompleted: true);

        await _packageRepo.UpdateCompletedFlagAsync(packageId, false);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(packageId);
        Assert.False(reloaded!.IsCompleted);
    }

    [Fact]
    public async Task GetIncompleteAsync_ReturnsOnlyPackagesWithIsCompletedFalse()
    {
        int incompleteId = await InsertPackageAsync("incomplete", isCompleted: false);
        await InsertPackageAsync("done", isCompleted: true);

        UploadPackageDto[] rows = await _packageRepo.GetIncompleteAsync();

        Assert.Single(rows);
        Assert.Equal(incompleteId, rows[0].Id);
    }

    [Fact]
    public async Task GetCompletedAsync_ReturnsOnlyPackagesWithIsCompletedTrue()
    {
        int completedId = await InsertPackageAsync("done", isCompleted: true);
        await InsertPackageAsync("incomplete", isCompleted: false);

        UploadPackageDto[] rows = await _packageRepo.GetCompletedAsync();

        Assert.Single(rows);
        Assert.Equal(completedId, rows[0].Id);
    }

    [Fact]
    public async Task FindAsync_RoundTripsIsRemovedFromUploadsFlag()
    {
        // Mapping smoke-test: the new flag must survive insert + read on the package DTO.
        UploadPackageDto pkg = new()
        {
            Name = "x",
            CreatedDateTime = DateTime.Now,
            IsRemovedFromUploads = true,
        };
        await _packageRepo.InsertAsync(pkg);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(pkg.Id);

        Assert.True(reloaded!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task DeleteHiddenHistoryAsync_RemovesFilesHiddenFromBothTabs()
    {
        // File hidden from both tabs (IsRemovedFromUploads + IsHidden) — should be deleted.
        int pkgId = await InsertPackageAsync("pkg");
        int hiddenInBoth = await InsertFileAsync(pkgId, "hidden.iso", FileState.Completed);
        await _fileRepo.SoftRemoveFromUploadsAsync([hiddenInBoth]);
        await _fileRepo.HideAsync([hiddenInBoth]);

        // File visible in Uploaded only (IsHidden=false, IsRemovedFromUploads=true) — keep it.
        int uploadedOnly = await InsertFileAsync(pkgId, "in-uploaded.iso", FileState.Completed);
        await _fileRepo.SoftRemoveFromUploadsAsync([uploadedOnly]);

        // File visible in Uploads only (IsRemovedFromUploads=false, regardless of state) — keep it.
        int uploadsOnly = await InsertFileAsync(pkgId, "in-uploads.iso", FileState.Failed);

        (int filesDeleted, int packagesDeleted) = await _packageRepo.DeleteHiddenHistoryAsync();

        Assert.Equal(1, filesDeleted);
        Assert.Equal(0, packagesDeleted); // package still has 2 visible files
        Assert.Null(await _fileRepo.FindAsync(hiddenInBoth));
        Assert.NotNull(await _fileRepo.FindAsync(uploadedOnly));
        Assert.NotNull(await _fileRepo.FindAsync(uploadsOnly));
    }

    [Fact]
    public async Task DeleteHiddenHistoryAsync_RemovesFailedFilesRemovedFromUploads()
    {
        // Failed/Cancelled files removed from the Uploads tab never qualified for the
        // Uploaded tab (state != Completed) so they're invisible everywhere → delete.
        int pkgId = await InsertPackageAsync("pkg");
        int failed = await InsertFileAsync(pkgId, "failed.iso", FileState.Failed);
        int cancelled = await InsertFileAsync(pkgId, "cancelled.iso", FileState.Cancelled);
        await _fileRepo.SoftRemoveFromUploadsAsync([failed, cancelled]);

        (int filesDeleted, _) = await _packageRepo.DeleteHiddenHistoryAsync();

        Assert.Equal(2, filesDeleted);
    }

    [Fact]
    public async Task DeleteHiddenHistoryAsync_DeletesOrphanPackagesAfterFiles()
    {
        // Package whose only file gets hard-deleted should also be removed — keeping the
        // shell row would leave a phantom in any package-level query.
        int orphanedPkg = await InsertPackageAsync("orphan");
        int fileId = await InsertFileAsync(orphanedPkg, "x.iso", FileState.Completed);
        await _fileRepo.SoftRemoveFromUploadsAsync([fileId]);
        await _fileRepo.HideAsync([fileId]);

        (int filesDeleted, int packagesDeleted) = await _packageRepo.DeleteHiddenHistoryAsync();

        Assert.Equal(1, filesDeleted);
        Assert.Equal(1, packagesDeleted);
        Assert.Null(await _packageRepo.FindAsync(orphanedPkg));
    }

    [Fact]
    public async Task DeleteHiddenHistoryAsync_NothingToDelete_ReturnsZeros()
    {
        int pkgId = await InsertPackageAsync("pkg");
        await InsertFileAsync(pkgId, "active.iso", FileState.Uploading);

        (int filesDeleted, int packagesDeleted) = await _packageRepo.DeleteHiddenHistoryAsync();

        Assert.Equal(0, filesDeleted);
        Assert.Equal(0, packagesDeleted);
    }

    private async Task<int> InsertPackageAsync(string name, bool isCompleted = false)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = isCompleted,
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
