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
    public async Task InsertAndFind_RoundTripsPriority()
    {
        UploadPackageDto pkg = new()
        {
            Name = "p",
            CreatedDateTime = DateTime.Now,
            Priority = PackagePriority.High,
        };
        await _packageRepo.InsertAsync(pkg);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(pkg.Id);

        Assert.Equal(PackagePriority.High, reloaded!.Priority);
    }

    [Fact]
    public async Task UpdatePriorityAsync_FlipsValueWithoutTouchingOtherFields()
    {
        UploadPackageDto pkg = new()
        {
            Name = "p",
            CreatedDateTime = DateTime.Now,
            Priority = PackagePriority.Normal,
            IsCompleted = true,
        };
        await _packageRepo.InsertAsync(pkg);

        await _packageRepo.UpdatePriorityAsync(pkg.Id, PackagePriority.Lowest);

        UploadPackageDto? reloaded = await _packageRepo.FindAsync(pkg.Id);
        Assert.Equal(PackagePriority.Lowest, reloaded!.Priority);
        Assert.True(reloaded.IsCompleted);
        Assert.Equal("p", reloaded.Name);
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
