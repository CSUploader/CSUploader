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
        UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);

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

            UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);

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
    public async Task GoNext_FilesMode_WithFiles_AdvancesAndDefaultsTitle()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA]);
            UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);
            vm.BrowseFilesCommand.Execute(null);
            vm.PackageTitle = string.Empty; // pretend user cleared it

            await vm.GoNextCommand.ExecuteAsync(null);

            Assert.Equal(1, vm.CurrentStep);
            Assert.Equal(Path.GetFileNameWithoutExtension(tempA), vm.PackageTitle);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public void AddMoreFiles_DedupesByFullPath_CaseInsensitive()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA])
                .Returns([tempA.ToUpperInvariant()]);

            UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);
            vm.BrowseFilesCommand.Execute(null);
            vm.AddMoreFilesCommand.Execute(null);

            Assert.Single(vm.Files);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public void FilesCountText_UpdatesWhenFilesChange()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFiles(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([tempA]);

            UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);
            string before = vm.FilesCountText;

            vm.BrowseFilesCommand.Execute(null);

            Assert.NotEqual(before, vm.FilesCountText);
            Assert.Contains("1", vm.FilesCountText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public void AddMoreFiles_DuplicateFilenameDifferentFolder_ShowsFolderSuffix()
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

            UploadWizardViewModel vm = CreateVm(dialog.Object, UploadWizardMode.Files);
            vm.BrowseFilesCommand.Execute(null);
            vm.AddMoreFilesCommand.Execute(null);

            Assert.Equal(2, vm.Files.Count);
            Assert.Contains(vm.Files, f => f.RelativePath.Contains(Path.GetFileName(dirB), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    private UploadWizardViewModel CreateVm(IDialogService dialog, UploadWizardMode mode = UploadWizardMode.Directory) =>
        new(_packageManager, _loginRepo, dialog, Mock.Of<IAppLogger>(), new AppSettings(), mode);

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
