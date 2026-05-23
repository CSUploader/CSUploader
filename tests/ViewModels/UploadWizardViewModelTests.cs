// <copyright file="UploadWizardViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
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

public class UploadWizardViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    public UploadWizardViewModelTests()
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

        _loginRepo = new FileHosterLoginRepository(_factory);
        AppSettings settings = new();
        DefaultFileHosterRegistry registry = new([]);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        _packageManager = new PackageManager(
            settings,
            _scheduler,
            new UploadPackageRepository(_factory),
            new UploadPackageFileRepository(_factory),
            _loginRepo,
            Mock.Of<IAppLogger>(),
            registry);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddAccountForHoster_WhenDialogCancelled_LeavesRowEmpty()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialog(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns((FileHosterLoginDto?)null);

        UploadWizardViewModel vm = CreateVm(dialog.Object);
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.FileHosters.Add(row);

        await vm.AddAccountForHosterCommand.ExecuteAsync(row);

        Assert.False(row.HasAccounts);
        Assert.False(row.Use);
        FileHosterLoginDto[] persisted = await _loginRepo.FindAsync("Rapidgator");
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task AddAccountForHoster_WhenSaved_PersistsAndAutoTicksUse()
    {
        FileHosterLoginDto saved = new()
        {
            FileHosterName = "Rapidgator",
            Username = "alice",
            Password = "pw",
        };

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialog("Rapidgator", It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns(saved);

        UploadWizardViewModel vm = CreateVm(dialog.Object);
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.FileHosters.Add(row);

        await vm.AddAccountForHosterCommand.ExecuteAsync(row);

        // Persisted to DB
        FileHosterLoginDto[] persisted = await _loginRepo.FindAsync("Rapidgator");
        Assert.Single(persisted);
        Assert.Equal("alice", persisted[0].Username);

        // Row VM was refreshed and auto-ticked
        Assert.True(row.HasAccounts);
        Assert.True(row.Use);
        Assert.NotNull(row.SelectedAccount);
        Assert.Equal("alice", row.SelectedAccount!.Username);
    }

    [Fact]
    public async Task AddAccountForHoster_WhenInvokedWithNull_NoOps()
    {
        Mock<IDialogService> dialog = new();
        UploadWizardViewModel vm = CreateVm(dialog.Object);

        await vm.AddAccountForHosterCommand.ExecuteAsync(null);

        dialog.Verify(
            d => d.ShowAddAccountDialog(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task GoNext_FilesMode_NoFiles_ShowsValidationError()
    {
        Mock<IDialogService> dialog = new();
        UploadWizardViewModel vm = CreateVm(dialog.Object);
        vm.Mode = UploadWizardMode.Files;

        await vm.GoNextCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.CurrentStep);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void BrowseFiles_PopulatesFilesAndDefaultsTitle()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;

            vm.BrowseFilesCommand.Execute(null);

            Assert.Single(vm.Files);
            Assert.Equal(Path.GetFileNameWithoutExtension(tempA), vm.PackageTitle);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public async Task GoNext_FilesMode_WithFiles_Advances()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA]);
            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;
            vm.BrowseFilesCommand.Execute(null);
            // PackageTitle was defaulted from filename by BrowseFiles; leave it intact

            await vm.GoNextCommand.ExecuteAsync(null);

            Assert.Equal(1, vm.CurrentStep);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public void BrowseFiles_AppendsAndDedupesByFullPath_CaseInsensitive()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA])
                .Returns([tempA.ToUpperInvariant()]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;
            vm.BrowseFilesCommand.Execute(null);
            vm.BrowseFilesCommand.Execute(null);

            Assert.Single(vm.Files);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public void BrowseFiles_DuplicateFilenameDifferentFolder_ShowsFolderSuffix()
    {
        string dirA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string dirB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        string fileA = Path.Combine(dirA, "data.bin");
        string fileB = Path.Combine(dirB, "data.bin");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([fileA])
                .Returns([fileB]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;
            vm.BrowseFilesCommand.Execute(null);
            vm.BrowseFilesCommand.Execute(null);

            Assert.Equal(2, vm.Files.Count);
            Assert.Contains(vm.Files, f => f.RelativePath.Contains(Path.GetFileName(dirB), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public void BrowseFiles_DoesNotClearExistingFiles()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        string tempB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA])
                .Returns([tempB]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;
            vm.BrowseFilesCommand.Execute(null);
            Assert.Single(vm.Files);

            vm.BrowseFilesCommand.Execute(null);

            Assert.Equal(2, vm.Files.Count);
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void DirectoryPath_WhenSetToValidDirectory_PopulatesFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.bin");
        string b = Path.Combine(dir, "b.bin");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        try
        {
            Mock<IDialogService> dialog = new();
            UploadWizardViewModel vm = CreateVm(dialog.Object);

            vm.DirectoryPath = dir;

            Assert.Equal(2, vm.Files.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ModeChange_ClearsFilesAndDirectoryPath()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            vm.Mode = UploadWizardMode.Files;
            vm.BrowseFilesCommand.Execute(null);
            vm.DirectoryPath = "C:\\should-be-cleared";
            Assert.Single(vm.Files);

            vm.Mode = UploadWizardMode.Directory;

            Assert.Empty(vm.Files);
            Assert.Equal(string.Empty, vm.DirectoryPath);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    private UploadWizardViewModel CreateVm(IDialogService dialog) =>
        new(_packageManager, _loginRepo, dialog, Mock.Of<IAppLogger>(), new AppSettings());

    [Fact]
    public void HosterValidation_OversizedFile_ListsFilenameAndDoesNotBlockNext()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.FileHosters.Add(brupload);

        FileEntry small = new() { FullPath = "small.iso", FileName = "small.iso", Size = 100, IsSelected = true };
        FileEntry huge = new() { FullPath = "huge.iso", FileName = "huge.iso", Size = 2L * 1024 * 1024 * 1024, IsSelected = true };
        vm.Files.Add(small);
        vm.Files.Add(huge);

        Assert.Empty(vm.HosterValidationWarnings);

        brupload.Use = true;

        // Warning must name the oversized file and say it won't be uploaded.
        string warning = Assert.Single(vm.HosterValidationWarnings);
        Assert.Contains("huge.iso", warning, StringComparison.Ordinal);
        Assert.Contains("won't be uploaded", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("small.iso", warning, StringComparison.Ordinal);

        // Next stays enabled because small.iso is still eligible.
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);

        // Deselecting the oversized file clears the warning entirely.
        huge.IsSelected = false;
        Assert.Empty(vm.HosterValidationWarnings);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_AllFilesTooBig_BlocksNextEvenWithSingleHoster()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.FileHosters.Add(brupload);

        vm.Files.Add(new FileEntry { FullPath = "a.iso", FileName = "a.iso", Size = 2L * 1024 * 1024 * 1024, IsSelected = true });
        vm.Files.Add(new FileEntry { FullPath = "b.iso", FileName = "b.iso", Size = 3L * 1024 * 1024 * 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 1;

        Assert.NotEmpty(vm.HosterValidationWarnings);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_FlagsTooManyFilesPerPackage()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.FileHosters.Add(brupload);

        for (int i = 0; i < 31; i++)
        {
            vm.Files.Add(new FileEntry { FullPath = $"f{i}.bin", FileName = $"f{i}.bin", Size = 1024, IsSelected = true });
        }

        brupload.Use = true;

        Assert.Contains(vm.HosterValidationWarnings, w => w.Contains("31", StringComparison.Ordinal) && w.Contains("30", StringComparison.Ordinal));
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
