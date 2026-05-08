// <copyright file="UploadWizardViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
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
        _scheduler = new UploadScheduler(settings);
        _packageManager = new PackageManager(
            settings,
            _scheduler,
            new UploadPackageRepository(_factory),
            new UploadPackageFileRepository(_factory),
            _loginRepo,
            Mock.Of<IAppLogger>());
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

    private UploadWizardViewModel CreateVm(IDialogService dialog) =>
        new(_packageManager, _loginRepo, dialog, Mock.Of<IAppLogger>());

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
