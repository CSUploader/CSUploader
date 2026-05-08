// <copyright file="UploadedViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

public class UploadedViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    public UploadedViewModelTests()
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

        _fileRepo = new UploadPackageFileRepository(_factory);
        _packageRepo = new UploadPackageRepository(_factory);

        AppSettings settings = new();
        IAppLogger logger = Mock.Of<IAppLogger>();
        FileHosterLoginRepository loginRepo = new(_factory);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), new CSUploader.Upload.Pipeline.DefaultFileHosterRegistry([]));
        _packageManager = new PackageManager(settings, _scheduler, _packageRepo, _fileRepo, loginRepo, logger);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WhenConfirmed_HidesRowInsteadOfDeleting()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        UploadedViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        UploadedFileRow row = vm.Files.Single(r => r.FileId == fileId);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<UploadedFileRow> { row });

        UploadPackageFileDto? dbRow = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(dbRow);
        Assert.True(dbRow!.IsHidden);
        Assert.DoesNotContain(vm.Files, r => r.FileId == fileId);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WhenDeclined_LeavesRowVisibleAndUnhidden()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        UploadedViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        UploadedFileRow row = vm.Files.Single(r => r.FileId == fileId);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<UploadedFileRow> { row });

        UploadPackageFileDto? dbRow = await _fileRepo.FindAsync(fileId);
        Assert.False(dbRow!.IsHidden);
        Assert.Contains(vm.Files, r => r.FileId == fileId);
    }

    [Fact]
    public async Task RemoveSelectedAsync_DoesNotDeleteParentPackage()
    {
        // The Hide path must not touch the package row, even if every file in it is hidden.
        // (Pre-soft-delete logic deleted empty packages — we want history preserved.)
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "only.iso", FileState.Completed);

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        UploadedViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        UploadedFileRow row = vm.Files.Single(r => r.FileId == fileId);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<UploadedFileRow> { row });

        UploadPackageDto? pkg = await _packageRepo.FindAsync(packageId);
        Assert.NotNull(pkg);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WithEmptyList_DoesNothing()
    {
        Mock<IDialogService> dialog = new();
        UploadedViewModel vm = CreateVm(dialog.Object);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<UploadedFileRow>());

        dialog.Verify(d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private UploadedViewModel CreateVm(IDialogService dialogService) =>
        new(_packageRepo, _fileRepo, _packageManager, dialogService, Mock.Of<IAppLogger>());

    private async Task<int> InsertPackageAsync(string name)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = false,
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
