// <copyright file="UploadPackageFileRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class UploadPackageFileRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly UploadPackageRepository _packageRepo;

    public UploadPackageFileRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _fileRepo = new UploadPackageFileRepository(_factory);
        _packageRepo = new UploadPackageRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HideAsync_FlipsIsHiddenFlagWithoutDeleting()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        int affected = await _fileRepo.HideAsync(new[] { fileId });

        Assert.Equal(1, affected);
        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsHidden);
    }

    [Fact]
    public async Task HideAsync_OnlyHidesSpecifiedIds()
    {
        int packageId = await InsertPackageAsync("pkg");
        int hidden = await InsertFileAsync(packageId, "hidden.iso", FileState.Completed);
        int kept = await InsertFileAsync(packageId, "kept.iso", FileState.Completed);

        await _fileRepo.HideAsync(new[] { hidden });

        UploadPackageFileDto? hiddenDto = await _fileRepo.FindAsync(hidden);
        UploadPackageFileDto? keptDto = await _fileRepo.FindAsync(kept);
        Assert.True(hiddenDto!.IsHidden);
        Assert.False(keptDto!.IsHidden);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_ReturnsOnlyTerminalStates()
    {
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "completed.iso", FileState.Completed);
        await InsertFileAsync(packageId, "failed.iso", FileState.Failed);
        await InsertFileAsync(packageId, "cancelled.iso", FileState.Cancelled);
        await InsertFileAsync(packageId, "idle.iso", FileState.Idle);
        await InsertFileAsync(packageId, "uploading.iso", FileState.Uploading);
        await InsertFileAsync(packageId, "hashing.iso", FileState.Hashing);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        string[] returnedNames = [.. rows.Select(r => r.File.FileName ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.Equal(new[] { "cancelled.iso", "completed.iso", "failed.iso" }, returnedNames);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_ExcludesHiddenRows()
    {
        int packageId = await InsertPackageAsync("pkg");
        int visible = await InsertFileAsync(packageId, "visible.iso", FileState.Completed);
        int hidden = await InsertFileAsync(packageId, "hidden.iso", FileState.Completed);

        await _fileRepo.HideAsync(new[] { hidden });

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        Assert.Single(rows);
        Assert.Equal("visible.iso", rows[0].File.FileName);
        Assert.Equal(visible, rows[0].File.Id);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_JoinsPackageNameFromOwningPackage()
    {
        int pkgA = await InsertPackageAsync("Movies");
        int pkgB = await InsertPackageAsync("Music");
        await InsertFileAsync(pkgA, "movie.mkv", FileState.Completed);
        await InsertFileAsync(pkgB, "song.mp3", FileState.Completed);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        var byName = rows.ToDictionary(r => r.File.FileName ?? string.Empty, r => r.PackageName, StringComparer.Ordinal);
        Assert.Equal("Movies", byName["movie.mkv"]);
        Assert.Equal("Music", byName["song.mp3"]);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_DoesNotFilterByPackageIsCompleted()
    {
        // Even when the parent package is still IsCompleted=false, the file row should
        // appear as soon as it reaches a terminal state. This is the regression that
        // the per-file Uploaded-tab refresh depends on.
        int packageId = await InsertPackageAsync("pkg", isCompleted: false);
        await InsertFileAsync(packageId, "done.iso", FileState.Completed);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        Assert.Single(rows);
        Assert.Equal("done.iso", rows[0].File.FileName);
    }

    private async Task<int> InsertPackageAsync(string name, bool isCompleted = false)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = isCompleted,
            DirectoryPath = string.Empty,
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
