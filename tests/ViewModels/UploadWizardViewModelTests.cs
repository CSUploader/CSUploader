// <copyright file="UploadWizardViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
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
        dialog.Setup(d => d.ShowAddAccountDialogAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(), It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .ReturnsAsync((FileHosterLoginDto?)null);

        UploadWizardViewModel vm = CreateVm(dialog.Object);
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

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
        dialog.Setup(d => d.ShowAddAccountDialogAsync("Rapidgator", It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(), It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .ReturnsAsync(saved);

        UploadWizardViewModel vm = CreateVm(dialog.Object);
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

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

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(null);

        dialog.Verify(
            d => d.ShowAddAccountDialogAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(), It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()),
            Times.Never);
    }

    [Fact]
    public async Task GoNext_WithNoFiles_ShowsValidationError()
    {
        Mock<IDialogService> dialog = new();
        UploadWizardViewModel vm = CreateVm(dialog.Object);

        await vm.GoNextCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.CurrentStep);
        dialog.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task BrowseFiles_PopulatesFilesAndDefaultsTitle()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([tempA]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);

            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            Assert.Single(vm.Sources.Files);
            Assert.Equal(Path.GetFileNameWithoutExtension(tempA), vm.Sources.PackageTitle);
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
            dialog.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([tempA]);
            UploadWizardViewModel vm = CreateVm(dialog.Object);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);
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
    public async Task BrowseFiles_AppendsAndDedupesByFullPath_CaseInsensitive()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([tempA])
                .ReturnsAsync([tempA.ToUpperInvariant()]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            Assert.Single(vm.Sources.Files);
        }
        finally
        {
            File.Delete(tempA);
        }
    }

    [Fact]
    public async Task BrowseFiles_DuplicateFilenameDifferentFolder_ShowsFolderSuffix()
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
            dialog.SetupSequence(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([fileA])
                .ReturnsAsync([fileB]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Sources.Files.Count);
            Assert.Contains(vm.Sources.Files, f => f.RelativePath.Contains(Path.GetFileName(dirB), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public async Task BrowseFiles_DoesNotClearExistingFiles()
    {
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        string tempB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.SetupSequence(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([tempA])
                .ReturnsAsync([tempB]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);
            Assert.Single(vm.Sources.Files);

            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Sources.Files.Count);
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public async Task AddFolders_PopulatesFilesAndRecordsTheSource()
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
            dialog.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([dir]);
            UploadWizardViewModel vm = CreateVm(dialog.Object);

            await vm.Sources.AddFoldersCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Sources.Files.Count);

            // The folder is remembered as a source, with what it contributed, so it can be taken back.
            UploadSource source = Assert.Single(vm.Sources.Sources);
            Assert.Equal(dir, source.Path);
            Assert.True(source.IsFolder);
            Assert.Equal(2, source.FileCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddingASecondSource_AppendsInsteadOfReplacing()
    {
        // This replaces a test that asserted the opposite: switching mode used to CLEAR the list, and
        // picking a second folder replaced the first. That was the bug this change fixes — a package
        // routinely draws from more than one place.
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "in-folder.bin"), "x");
        string loose = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(loose, "y");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([dir]);
            dialog.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([loose]);

            UploadWizardViewModel vm = CreateVm(dialog.Object);

            await vm.Sources.AddFoldersCommand.ExecuteAsync(null);
            Assert.Single(vm.Sources.Files);

            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Sources.Files.Count);
            Assert.Equal(2, vm.Sources.Sources.Count);
            Assert.Contains(vm.Sources.Files, f => f.FullPath == loose);
            Assert.Contains(vm.Sources.Files, f => f.FileName == "in-folder.bin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            File.Delete(loose);
        }
    }


    // ── Adding an account from the wizard checks it first ─────────────────────────────────────────
    //
    // The check itself now happens INSIDE the dialog, so a rejected password can be corrected in
    // place. What this view-model is responsible for is handing the dialog a working validator, and
    // saving only what comes back — the dialog returns null unless the credentials were proved.

    [Fact]
    public async Task AddAccountForHoster_HandsTheDialogAValidator_ThatChecksWhatWasTyped()
    {
        Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? captured = null;

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialogAsync(
                "Rapidgator", It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(),
                It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .Callback((string _, string[] _, Func<string, Task<AccountCheckResult>> _, string? _, Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? v) => captured = v)
            .ReturnsAsync((FileHosterLoginDto?)null);

        Mock<IAccountVerifier> verifier = new();
        verifier.Setup(v => v.CheckAsync("Rapidgator", "alice", "pw", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "ok"));

        UploadWizardViewModel vm = CreateVm(dialog.Object, verifier.Object);
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

        Assert.NotNull(captured);

        // The delegate must carry the typed credentials through to the verifier — that is the whole
        // point of it, and the dialog can only pass on what it was given.
        AccountCheckResult result = await captured!(
            new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "alice", Password = "pw" },
            CancellationToken.None);

        Assert.True(result.IsValid);
        verifier.Verify(v => v.CheckAsync("Rapidgator", "alice", "pw", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddAccountForHoster_WithNoVerifier_HandsNoValidator_SoSaveStillWorks()
    {
        // A hoster this app cannot check must not become unaddable — Save closes at once instead.
        bool sawValidator = true;

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialogAsync(
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(),
                It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .Callback((string _, string[] _, Func<string, Task<AccountCheckResult>> _, string? _, Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? v) => sawValidator = v is not null)
            .ReturnsAsync(new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "alice", Password = "pw" });

        UploadWizardViewModel vm = CreateVm(dialog.Object);   // no verifier
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

        Assert.False(sawValidator);
        Assert.Single(await _loginRepo.FindAsync("Rapidgator"));
        Assert.True(row.Use);
    }

    [Fact]
    public async Task AddAccountForHoster_ADialogThatReturnsNothing_SavesNothing()
    {
        // What the dialog does when the check fails and the user gives up: the account was never
        // proved, so nothing is written and the row is left alone.
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialogAsync(
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(),
                It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .ReturnsAsync((FileHosterLoginDto?)null);

        UploadWizardViewModel vm = CreateVm(dialog.Object, Mock.Of<IAccountVerifier>());

        // Anonymous-capable on purpose: CanUse stays true here, so nothing else would stop Use being
        // ticked for a hoster the user never got an account on.
        FileHosterSelectionViewModel row = new("Catbox", [], supportsAnonymous: true);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

        Assert.Empty(await _loginRepo.FindAsync("Catbox"));
        Assert.False(row.Use);
    }

    [Fact]
    public async Task AddAccountForHoster_SavesTheVerifiedAccountTheDialogReturns()
    {
        // The dialog stamps the check's result on before returning — including the derived credential
        // that FileMirage, DropMB and FileCat have no other source for — and it must reach the DB.
        FileHosterLoginDto verified = new()
        {
            FileHosterName = "Rapidgator",
            Username = "alice",
            Password = "pw",
            ApiKey = "DERIVED-KEY",
            SessionCookie = "sess-abc",
            AccountType = AccountType.Premium,
        };

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialogAsync(
                "Rapidgator", It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(),
                It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .ReturnsAsync(verified);

        UploadWizardViewModel vm = CreateVm(dialog.Object, Mock.Of<IAccountVerifier>());
        FileHosterSelectionViewModel row = new("Rapidgator", []);
        vm.Hosters.FileHosters.Add(row);

        await vm.Hosters.AddAccountForHosterCommand.ExecuteAsync(row);

        FileHosterLoginDto persisted = Assert.Single(await _loginRepo.FindAsync("Rapidgator"));
        Assert.Equal("DERIVED-KEY", persisted.ApiKey);
        Assert.Equal("sess-abc", persisted.SessionCookie);
        Assert.Equal(AccountType.Premium, persisted.AccountType);
        Assert.NotNull(persisted.CreatedDateTime);

        Assert.True(row.Use);
        Assert.True(row.HasAccounts);
    }

    private UploadWizardViewModel CreateVm(IDialogService dialog) =>
        new(_packageManager, _loginRepo, dialog, Mock.Of<IAppLogger>(), new AppSettings());

    private UploadWizardViewModel CreateVm(IDialogService dialog, IAccountVerifier verifier) =>
        new(_packageManager, _loginRepo, dialog, Mock.Of<IAppLogger>(), new AppSettings(), null, verifier);

    /// <summary>An "Add account…" that returns these credentials for the given hoster.</summary>
    private static Mock<IDialogService> DialogReturning(string hoster, FileHosterLoginDto dto)
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowAddAccountDialogAsync(hoster, It.IsAny<string[]>(), It.IsAny<Func<string, Task<AccountCheckResult>>>(), It.IsAny<string?>(), It.IsAny<Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>?>()))
            .ReturnsAsync(dto);
        return dialog;
    }

    [Fact]
    public void RemoveSelectedFiles_DropsSelectedRows_LeavesUnselected()
    {
        // Mirror what the XAML wires up: pass an IList of FileEntry (DataGrid.SelectedItems
        // is a non-generic IList in WPF). After Remove, the Files collection must contain
        // only the entries the user DIDN'T have selected in the grid.
        UploadWizardViewModel vm = CreateVm(Mock.Of<IDialogService>());
        FileEntry a = new() { FullPath = "a.bin", FileName = "a.bin", Size = 10, IsSelected = true };
        FileEntry b = new() { FullPath = "b.bin", FileName = "b.bin", Size = 20, IsSelected = true };
        FileEntry c = new() { FullPath = "c.bin", FileName = "c.bin", Size = 30, IsSelected = true };
        vm.Sources.Files.Add(a);
        vm.Sources.Files.Add(b);
        vm.Sources.Files.Add(c);

        // Grid selects a + c only.
        System.Collections.IList selected = new System.Collections.ArrayList { a, c };
        vm.Sources.RemoveSelectedFilesCommand.Execute(selected);

        FileEntry only = Assert.Single(vm.Sources.Files);
        Assert.Same(b, only);
    }

    [Fact]
    public void RemoveSelectedFiles_NullOrEmptySelection_IsNoOp()
    {
        UploadWizardViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 10 });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 20 });

        vm.Sources.RemoveSelectedFilesCommand.Execute(null);
        Assert.Equal(2, vm.Sources.Files.Count);

        vm.Sources.RemoveSelectedFilesCommand.Execute(new System.Collections.ArrayList());
        Assert.Equal(2, vm.Sources.Files.Count);
    }

    [Fact]
    public void HosterValidation_OversizedFile_ListsFilenameAndDoesNotBlockNext()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);

        FileEntry small = new() { FullPath = "small.iso", FileName = "small.iso", Size = 100, IsSelected = true };
        FileEntry huge = new() { FullPath = "huge.iso", FileName = "huge.iso", Size = 2L * 1024 * 1024 * 1024, IsSelected = true };
        vm.Sources.Files.Add(small);
        vm.Sources.Files.Add(huge);

        Assert.Empty(vm.Hosters.HosterValidationWarnings);

        brupload.Use = true;

        // Warning must name the oversized file and say it won't be uploaded.
        string warning = Assert.Single(vm.Hosters.HosterValidationWarnings);
        Assert.Contains("huge.iso", warning, StringComparison.Ordinal);
        Assert.Contains("won't be uploaded", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("small.iso", warning, StringComparison.Ordinal);

        // Next stays enabled because small.iso is still eligible.
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);

        // Deselecting the oversized file clears the warning entirely.
        huge.IsSelected = false;
        Assert.Empty(vm.Hosters.HosterValidationWarnings);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_AllFilesTooBig_BlocksNextEvenWithSingleHoster()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);

        vm.Sources.Files.Add(new FileEntry { FullPath = "a.iso", FileName = "a.iso", Size = 2L * 1024 * 1024 * 1024, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.iso", FileName = "b.iso", Size = 3L * 1024 * 1024 * 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 1;

        Assert.NotEmpty(vm.Hosters.HosterValidationWarnings);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_FlagsTooManyFilesPerPackage()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);

        for (int i = 0; i < 31; i++)
        {
            vm.Sources.Files.Add(new FileEntry { FullPath = $"f{i}.bin", FileName = $"f{i}.bin", Size = 1024, IsSelected = true });
        }

        brupload.Use = true;

        Assert.Contains(vm.Hosters.HosterValidationWarnings, w => w.Contains("31", StringComparison.Ordinal) && w.Contains("30", StringComparison.Ordinal));
    }

    [Fact]
    public void HosterValidation_RejectedFilename_ListsFilenameAndDoesNotBlockNext()
    {
        // Buzzheavier rejects '#'/';' in a name — the wizard warns and drops that file, exactly like an
        // oversized one, while leaving the clean-named file eligible so Next stays enabled.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 1, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(bz);

        FileEntry clean = new() { FullPath = "clean.mkv", FileName = "clean.mkv", Size = 100, IsSelected = true };
        FileEntry bad = new() { FullPath = "ep #1.mkv", FileName = "ep #1.mkv", Size = 200, IsSelected = true };
        vm.Sources.Files.Add(clean);
        vm.Sources.Files.Add(bad);

        Assert.Empty(vm.Hosters.HosterValidationWarnings);

        bz.Use = true;

        string warning = Assert.Single(vm.Hosters.HosterValidationWarnings);
        Assert.Contains("ep #1.mkv", warning, StringComparison.Ordinal);
        Assert.Contains("won't be uploaded", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("clean.mkv", warning, StringComparison.Ordinal);

        // Next stays enabled because clean.mkv is still eligible.
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);

        // Deselecting the rejected-name file clears the warning entirely.
        bad.IsSelected = false;
        Assert.Empty(vm.Hosters.HosterValidationWarnings);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_ExtensionRules_WarnOnStep1_BeforeNextIsPressed()
    {
        // The reported gap: three hosters enforce EXTENSION rules the wizard knew nothing about, so a
        // .r00 or .avi sailed through selection and only failed once its upload had already started
        // (or, on Uploadrar and filedot, after the whole file had transferred).
        //
        // qu.ax runs an ALLOWLIST — .rar passes, .r00/.sfv/.nfo do not — which is the harshest case
        // because a classic multi-part set is half-accepted.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.QuAxPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel qu = new("Qu.ax", [new FileHosterLoginDto { Id = 1, FileHosterName = "Qu.ax", Username = "u" }]);
        vm.Hosters.FileHosters.Add(qu);

        FileEntry part1 = new() { FullPath = "rls.part1.rar", FileName = "rls.part1.rar", Size = 100, IsSelected = true };
        FileEntry r00 = new() { FullPath = "rls.r00", FileName = "rls.r00", Size = 100, IsSelected = true };
        FileEntry sfv = new() { FullPath = "rls.sfv", FileName = "rls.sfv", Size = 10, IsSelected = true };
        vm.Sources.Files.Add(part1);
        vm.Sources.Files.Add(r00);
        vm.Sources.Files.Add(sfv);

        qu.Use = true;

        // Named, on the hoster step, without the user doing anything else.
        string warning = Assert.Single(vm.Hosters.HosterValidationWarnings);
        Assert.Contains("rls.r00", warning, StringComparison.Ordinal);
        Assert.Contains("rls.sfv", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("rls.part1.rar", warning, StringComparison.Ordinal);

        // …and it must blame the EXTENSION, not "characters". Buzzheavier's wording would send the
        // user hunting through "rls.r00" for an illegal character that isn't there. The negative is
        // the real guard; the positive pins the intent, so a future reword has to stop and ask
        // whether the new wording still distinguishes the two rules.
        Assert.Contains("extension", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("characters", warning, StringComparison.OrdinalIgnoreCase);

        // The .partN.rar volume is still eligible, so the run isn't blocked.
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public async Task LoadFileHosters_OffersOnlyAccountsLeftSwitchedOnInTheAccountManager()
    {
        // An account unticked in Settings → Accounts isn't a choice: everything downstream skips it, so
        // offering it in the wizard's picker only invites choosing a hoster that then uploads nothing.
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "BRupload", Username = "kept", Password = "p" });
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "BRupload", Username = "switchedoff", Password = "p", Disabled = true });
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "Buzzheavier", Username = "onlyone", Password = "p", Disabled = true });

        string temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        File.WriteAllText(temp, "x");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync([temp]);
            UploadWizardViewModel vm = CreateVm(dialog.Object);
            await vm.Sources.AddFilesCommand.ExecuteAsync(null);

            await vm.GoNextCommand.ExecuteAsync(null); // step 1 loads the hoster rows
            Assert.Equal(1, vm.CurrentStep);

            FileHosterSelectionViewModel br = vm.Hosters.FileHosters.First(h => h.FileHosterName == "BRupload");
            Assert.Equal("kept", Assert.Single(br.Accounts).Username);
            Assert.Equal("kept", br.SelectedAccount?.Username);

            // A hoster whose ONLY account is switched off reads as having none — which is the truth:
            // there is nothing there to upload with until it's switched back on.
            FileHosterSelectionViewModel bz = vm.Hosters.FileHosters.First(h => h.FileHosterName == "Buzzheavier");
            Assert.Empty(bz.Accounts);
            Assert.False(bz.HasAccounts);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void HosterValidation_AccountWhoseLastCheckFailed_IsExplainedOnTheHosterStep()
    {
        // The reported confusion: a hoster ticked on step 2 (its account named, its cap shown) and then
        // a summary reading "16 files won't be uploaded to any hoster" — the files named, the reason
        // never given. The two pages disagreed: the summary skipped the hoster for its account state,
        // this page knew nothing about that rule and let Next through.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterLoginDto account = new() { Id = 1, FileHosterName = "BRupload", Username = "someone" };
        FileHosterSelectionViewModel brupload = new("BRupload", [account]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 1024, IsSelected = true });

        brupload.Use = true;
        Assert.Empty(vm.Hosters.HosterValidationWarnings);

        account.MarkRefreshed(AccountCheckStatus.Failed, "nope", DateTime.Now);
        brupload.Use = false;
        brupload.Use = true; // re-tick to recompute, as toggling the checkbox does

        string warning = Assert.Single(vm.Hosters.HosterValidationWarnings);
        Assert.Contains("BRupload", warning, StringComparison.Ordinal);
        Assert.Contains("someone", warning, StringComparison.Ordinal);
        Assert.Contains("check", warning, StringComparison.OrdinalIgnoreCase);

        // It was the only hoster, so nothing can upload: Next is blocked HERE, with the sentence above,
        // rather than at the summary with no sentence at all.
        vm.CurrentStep = 1;
        Assert.False(vm.CanGoNext);

        // And the summary still agrees about the outcome — the file has nowhere to go.
        vm.CurrentStep = 2;
        Assert.Empty(vm.Summary.Summaries);
        Assert.Single(vm.Summary.OrphanFiles);
    }

    [Fact]
    public void HosterValidation_DisabledAccount_SaysSo_AndAnonymousIsNeverSkipped()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterLoginDto disabled = new() { Id = 1, FileHosterName = "BRupload", Username = "someone", Disabled = true };
        FileHosterSelectionViewModel brupload = new("BRupload", [disabled]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 1024, IsSelected = true });

        brupload.Use = true;

        // A different sentence from the failed-check one: switching an account off is the user's own
        // doing, so it doesn't tell them to go and check it.
        string warning = Assert.Single(vm.Hosters.HosterValidationWarnings);
        Assert.Contains("switched off", warning, StringComparison.OrdinalIgnoreCase);

        // The synthetic Anonymous selection carries no such state and must never be skipped — it has
        // no Disabled flag and is never checked, so treating it like an account would kill every
        // anonymous upload in the app.
        FileHosterLoginDto anonymous = new() { Id = 0, FileHosterName = "BRupload", IsAnonymous = true };
        FileHosterSelectionViewModel anon = new("BRupload", [anonymous]);
        vm.Hosters.FileHosters.Clear();
        vm.Hosters.FileHosters.Add(anon);
        anon.Use = true;

        Assert.Empty(vm.Hosters.HosterValidationWarnings);
    }

    [Fact]
    public void HosterValidation_UnusableAccount_DoesNotBlockWhenAnotherHosterCanUpload()
    {
        DefaultFileHosterRegistry registry = new([
            new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline(),
            new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline(),
        ]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterLoginDto failed = new() { Id = 1, FileHosterName = "BRupload", Username = "someone" };
        failed.MarkRefreshed(AccountCheckStatus.Failed, "nope", DateTime.Now);
        FileHosterSelectionViewModel brupload = new("BRupload", [failed]);
        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 2, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Hosters.FileHosters.Add(bz);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 1024, IsSelected = true });

        brupload.Use = true;
        bz.Use = true;

        // Warned about, but not a hard block: the file still has a destination.
        Assert.Single(vm.Hosters.HosterValidationWarnings);
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);

        vm.CurrentStep = 2;
        Assert.Equal("Buzzheavier", Assert.Single(vm.Summary.Summaries).HosterName);
        Assert.Empty(vm.Summary.OrphanFiles);
    }

    [Fact]
    public void HosterValidation_BlocklistHosters_AlsoWarnBeforeUploading()
    {
        // Uploadrar (video blocklist) and filedot (image blocklist) enforce their lists only at the
        // END of an upload, so without this the user pays the whole transfer to find out.
        DefaultFileHosterRegistry registry = new([
            new CSUploader.Upload.Pipeline.Hosters.UploadrarPipeline(),
            new CSUploader.Upload.Pipeline.Hosters.FiledotPipeline(),
        ]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel rar = new("Uploadrar", [new FileHosterLoginDto { Id = 1, FileHosterName = "Uploadrar", Username = "u" }]);
        FileHosterSelectionViewModel dot = new("Filedot", [new FileHosterLoginDto { Id = 2, FileHosterName = "Filedot", Username = "u" }]);
        vm.Hosters.FileHosters.Add(rar);
        vm.Hosters.FileHosters.Add(dot);

        FileEntry video = new() { FullPath = "clip.avi", FileName = "clip.avi", Size = 100, IsSelected = true };
        FileEntry image = new() { FullPath = "cover.jpg", FileName = "cover.jpg", Size = 100, IsSelected = true };
        FileEntry archive = new() { FullPath = "rls.rar", FileName = "rls.rar", Size = 100, IsSelected = true };
        vm.Sources.Files.Add(video);
        vm.Sources.Files.Add(image);
        vm.Sources.Files.Add(archive);

        rar.Use = true;
        dot.Use = true;

        // Each hoster names only ITS OWN refusals — the .avi is fine for filedot and the .jpg for
        // Uploadrar, which is why this is per-hoster rather than a global deselect.
        string rarWarning = Assert.Single(vm.Hosters.HosterValidationWarnings, w => w.Contains("Uploadrar", StringComparison.Ordinal));
        Assert.Contains("clip.avi", rarWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("cover.jpg", rarWarning, StringComparison.Ordinal);
        Assert.Contains("extension", rarWarning, StringComparison.OrdinalIgnoreCase);

        string dotWarning = Assert.Single(vm.Hosters.HosterValidationWarnings, w => w.Contains("Filedot", StringComparison.Ordinal));
        Assert.Contains("cover.jpg", dotWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("clip.avi", dotWarning, StringComparison.Ordinal);

        // rls.rar is acceptable to both, so nothing is blocked.
        vm.CurrentStep = 1;
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void HosterValidation_AllFilenamesRejected_BlocksNextEvenWithSingleHoster()
    {
        // Every file has a name Buzzheavier won't accept → nothing can upload, so Next must block
        // (parity with the all-too-big case).
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 1, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(bz);

        vm.Sources.Files.Add(new FileEntry { FullPath = "a;.mkv", FileName = "a;.mkv", Size = 100, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b#.mkv", FileName = "b#.mkv", Size = 200, IsSelected = true });

        bz.Use = true;
        vm.CurrentStep = 1;

        Assert.NotEmpty(vm.Hosters.HosterValidationWarnings);
        Assert.False(vm.CanGoNext);
    }

    // ── Summary step (CurrentStep == 2) ──

    [Fact]
    public void Summary_PopulatesOnStep2Transition_AndIncludesAcceptingHoster()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "testuser" }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 1024, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 2048, IsSelected = true });

        brupload.Use = true;

        // Summary is empty before entering step 2 — it's lazy-populated.
        Assert.Empty(vm.Summary.Summaries);

        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.Equal("BRupload", entry.HosterName);
        Assert.Equal("testuser", entry.AccountUsername);
        Assert.Equal(2, entry.FileCount);
        // TotalSize sums Files[].Size — surfaces in the expander header alongside count.
        Assert.Equal(1024 + 2048, entry.TotalSize);
        // MaxFileSize flows through from the pipeline so the header can render the cap.
        Assert.Equal(1L * 1024 * 1024 * 1024, entry.MaxFileSize);
        Assert.Empty(vm.Summary.OrphanFiles);
        Assert.False(vm.Summary.HasOrphanFiles);
    }

    [Fact]
    public void Summary_HosterWithAllFilesOversize_IsOmittedAndFilesBecomeOrphans()
    {
        // BRupload's MaxFileSize is 1 GiB; we feed two 2 GiB files. Hoster has 0 eligible
        // files, so it must NOT appear in Summaries and both files become orphans.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "big1.iso", FileName = "big1.iso", Size = 2L * 1024 * 1024 * 1024, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "big2.iso", FileName = "big2.iso", Size = 3L * 1024 * 1024 * 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 2;

        Assert.Empty(vm.Summary.Summaries);
        Assert.Equal(2, vm.Summary.OrphanFilesCount);
        Assert.True(vm.Summary.HasOrphanFiles);
    }

    [Fact]
    public void Summary_HosterWithSomeOversizeFiles_OmitsThemFromItsFileList()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "ok.bin", FileName = "ok.bin", Size = 1024, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "huge.bin", FileName = "huge.bin", Size = 2L * 1024 * 1024 * 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        SummaryFileItem only = Assert.Single(entry.Files);
        Assert.Equal("ok.bin", only.FileName);
        // huge.bin had nowhere to go — it's an orphan even though BRupload appeared.
        FileEntry orphan = Assert.Single(vm.Summary.OrphanFiles);
        Assert.Equal("huge.bin", orphan.FileName);
    }

    [Fact]
    public void Summary_HosterWithAllRejectedNames_IsOmittedAndFilesBecomeOrphans()
    {
        // Both files carry a character Buzzheavier rejects ('#'/';'): 0 eligible files, so the hoster
        // must NOT appear in Summaries and both files become orphans — same as the all-oversize case.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 1, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(bz);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a #1.mkv", FileName = "a #1.mkv", Size = 100, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b; two.mkv", FileName = "b; two.mkv", Size = 200, IsSelected = true });

        bz.Use = true;
        vm.CurrentStep = 2;

        Assert.Empty(vm.Summary.Summaries);
        Assert.Equal(2, vm.Summary.OrphanFilesCount);
        Assert.True(vm.Summary.HasOrphanFiles);
    }

    [Fact]
    public void Summary_HosterWithSomeRejectedNames_OmitsThemFromItsFileList()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 1, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(bz);
        vm.Sources.Files.Add(new FileEntry { FullPath = "ok.mkv", FileName = "ok.mkv", Size = 100, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "Paladin; Agateram.mkv", FileName = "Paladin; Agateram.mkv", Size = 200, IsSelected = true });

        bz.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        SummaryFileItem only = Assert.Single(entry.Files);
        Assert.Equal("ok.mkv", only.FileName);
        FileEntry orphan = Assert.Single(vm.Summary.OrphanFiles);
        Assert.Equal("Paladin; Agateram.mkv", orphan.FileName);
    }

    [Fact]
    public void Summary_AcceptedSpecialCharsFilename_StaysEligible()
    {
        // Regression guard: '@', '[', ']', '(', ')', '+' are all accepted by Buzzheavier — only '#'/';'
        // reject. A too-broad name filter would wrongly orphan this real-world name.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BuzzheavierPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel bz = new("Buzzheavier", [new FileHosterLoginDto { Id = 1, FileHosterName = "Buzzheavier", Username = "u" }]);
        vm.Hosters.FileHosters.Add(bz);
        vm.Sources.Files.Add(new FileEntry { FullPath = "s.mkv", FileName = "[BD] Show 05 (4k AV1@M10p DTS 2ch+5.1ch).mkv", Size = 100, IsSelected = true });

        bz.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.Single(entry.Files);
        Assert.Empty(vm.Summary.OrphanFiles);
    }

    [Fact]
    public void Summary_AutoFitsToAccountAvailableSpace_KeepingBiggest()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        // Account reports 1000 bytes free (quota 1000, used 0); files 600 + 300 + 300 → fit keeps 600 + 300.
        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 1000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "big.bin", FileName = "big.bin", Size = 600, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m1.bin", FileName = "m1.bin", Size = 300, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m2.bin", FileName = "m2.bin", Size = 300, IsSelected = true });
        brupload.Use = true;

        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.Equal(900L, entry.IncludedBytes);   // 600 + one 300
        Assert.Equal(2, entry.IncludedCount);
        Assert.False(entry.IsOverCapacity);
        Assert.True(vm.CanGoNext);                  // within capacity → Next allowed
        Assert.True(vm.Summary.HasAutoFitNotice);           // one file was auto-unchecked
        // Single constrained hoster → the banner names its free space so the user sees what it fit to.
        Assert.Contains("free", vm.Summary.AutoFitNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_ManualUncheck_OfAFittingFile_DoesNotShowAutoFitBanner()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        // Account reports 100000 bytes free — everything fits, so auto-fit evicts nothing.
        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 100000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 600, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 300, IsSelected = true });
        brupload.Use = true;

        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.False(vm.Summary.HasAutoFitNotice); // nothing was auto-evicted (everything fit)

        // The user unchecks a file by hand. It fit fine → this is NOT a space eviction, so the banner that
        // claims "unchecked to fit the available space" must stay hidden.
        entry.Files.First(f => f.Included).Included = false;

        Assert.False(vm.Summary.HasAutoFitNotice);
        Assert.Equal(string.Empty, vm.Summary.AutoFitNotice);
    }

    [Fact]
    public void Step1Footer_SelectedCountAndTotalSize_TrackTogglesAndRemovals()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        List<string> raised = [];
        vm.Sources.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 600, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 300, IsSelected = true });

        Assert.Equal(2, vm.Sources.SelectedFileCount);
        Assert.Equal(ByteUnit.FromBytes(900, ByteBase.Binary).ToFriendlyString(), vm.Sources.SelectedTotalSizeDisplay);

        // Unchecking a file must live-update BOTH footer stats (and raise change notifications for them —
        // that's what the Step-1 footer bindings ride on).
        raised.Clear();
        vm.Sources.Files[0].IsSelected = false;
        Assert.Equal(1, vm.Sources.SelectedFileCount);
        Assert.Equal(ByteUnit.FromBytes(300, ByteBase.Binary).ToFriendlyString(), vm.Sources.SelectedTotalSizeDisplay);
        Assert.Contains(nameof(vm.Sources.SelectedFileCount), raised);
        Assert.Contains(nameof(vm.Sources.SelectedTotalSizeDisplay), raised);

        // Removing the remaining ticked file zeroes the footer.
        vm.Sources.Files.RemoveAt(1);
        Assert.Equal(0, vm.Sources.SelectedFileCount);
        Assert.Equal(ByteUnit.FromBytes(0, ByteBase.Binary).ToFriendlyString(), vm.Sources.SelectedTotalSizeDisplay);
    }

    [Fact]
    public void StartMode_SwitchingToScheduled_PrefillsDateTimeToNow()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        Assert.Equal(UploadStartMode.Immediately, vm.StartMode);

        DateTime before = DateTime.Now;
        vm.StartMode = UploadStartMode.Scheduled;
        DateTime after = DateTime.Now;

        // Date is today (was tomorrow by default); InRange spans the before/after dates for the midnight edge.
        Assert.InRange(vm.ScheduledDate, before.Date, after.Date);

        // Time parses (HH:mm) and, combined with the date, sits at "now" truncated to the minute — i.e. within
        // the [before-1min, after] window (HH:mm drops up to 59s, never rounds up).
        Assert.True(TimeSpan.TryParse(vm.ScheduledTime, out TimeSpan t));
        DateTime filled = vm.ScheduledDate + t;
        Assert.InRange(filled, before.AddMinutes(-1), after.AddSeconds(1));
    }

    [Fact]
    public void LoadFiles_BulkDirectoryScan_RecomputesFooterOnce_NotPerFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csup-bulkload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 6; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), new byte[100]);
            }

            DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
            UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

            int footerNotifications = 0;
            vm.Sources.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Sources.SelectedFileCount))
                {
                    footerNotifications++;
                }
            };

            vm.Sources.AddDroppedPaths([dir]); // triggers the bulk directory walk

            // Correctness: the footer reflects the whole scan.
            Assert.Equal(6, vm.Sources.SelectedFileCount);
            Assert.Equal(ByteUnit.FromBytes(600, ByteBase.Binary).ToFriendlyString(), vm.Sources.SelectedTotalSizeDisplay);

            // Batching: the footer recomputes ONCE at the end of the scan, not once per file (was ~6+ → O(N²)).
            Assert.True(footerNotifications <= 2, $"expected footer to recompute at most twice, got {footerNotifications}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Summary_TotalUploadSummary_SumsIncludedAcrossHosters_AndUpdatesLive()
    {
        DefaultFileHosterRegistry registry = new(
        [
            new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline(),
            new CSUploader.Upload.Pipeline.Hosters.KatFilePipeline(),
        ]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        // Both accounts have ample space → no auto-fit; every file is included on every hoster.
        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 100_000L, StorageUsedBytes = 0L }]);
        FileHosterSelectionViewModel katfile = new(
            "KatFile",
            [new FileHosterLoginDto { Id = 2, FileHosterName = "KatFile", Username = "k", StorageQuotaBytes = 100_000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Hosters.FileHosters.Add(katfile);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 100, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 200, IsSelected = true });
        brupload.Use = true;
        katfile.Use = true;

        List<string> footers = [];
        vm.Summary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardSummaryViewModel.TotalUploadSummary))
            {
                footers.Add(vm.Summary.TotalUploadSummary);
            }
        };

        vm.CurrentStep = 2;

        // 2 hosters × 2 files = 4 uploads, 2 × (100 + 200) = 600 bytes — the grand total sums across hosters.
        Assert.Equal(2, vm.Summary.Summaries.Count);
        Assert.Equal(4, vm.Summary.Summaries.Sum(s => s.IncludedCount));
        Assert.Equal(600L, vm.Summary.Summaries.Sum(s => s.IncludedBytes));

        string expected = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Summary_TotalFooter_Format"],
            4,
            ByteUnit.FromBytes(600L, ByteBase.Binary).ToFriendlyString());
        Assert.Equal(expected, vm.Summary.TotalUploadSummary);

        // Unchecking one file on one hoster drops the grand total to 3 uploads / 500 bytes and re-raises the footer.
        footers.Clear();
        vm.Summary.Summaries[0].Files.First(f => f.Included && f.Size == 100).Included = false;
        Assert.Equal(3, vm.Summary.Summaries.Sum(s => s.IncludedCount));
        Assert.Equal(500L, vm.Summary.Summaries.Sum(s => s.IncludedBytes));

        string expectedAfter = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Summary_TotalFooter_Format"],
            3,
            ByteUnit.FromBytes(500L, ByteBase.Binary).ToFriendlyString());
        Assert.Equal(expectedAfter, vm.Summary.TotalUploadSummary);
        Assert.Contains(expectedAfter, footers);    // footer raised PropertyChanged on the toggle
    }

    [Fact]
    public void Summary_RecheckingFilePastAvailable_BlocksNext_UncheckingRestores()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 1000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "big.bin", FileName = "big.bin", Size = 600, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m1.bin", FileName = "m1.bin", Size = 300, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m2.bin", FileName = "m2.bin", Size = 300, IsSelected = true });
        brupload.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.True(vm.CanGoNext); // auto-fit kept it within capacity

        // User re-checks the auto-dropped file → 1200 > 1000 → Next blocked.
        entry.Files.First(f => !f.Included).Included = true;
        Assert.True(entry.IsOverCapacity);
        Assert.False(vm.CanGoNext);

        // Unchecking the 600 brings it back to 600 ≤ 1000 → Next allowed again.
        entry.Files.First(f => f.Included && f.Size == 600).Included = false;
        Assert.False(entry.IsOverCapacity);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void BuildIncludedFilesPerHoster_ContainsOnlyCheckedFiles()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 1000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "big.bin", FileName = "big.bin", Size = 600, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m1.bin", FileName = "m1.bin", Size = 300, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "m2.bin", FileName = "m2.bin", Size = 300, IsSelected = true });
        brupload.Use = true;
        vm.CurrentStep = 2; // populates Summaries + auto-fits (keeps 600 + one 300, drops the other)

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        string droppedPath = entry.Files.First(f => !f.Included).File.FullPath;

        Dictionary<string, HashSet<string>>? map = vm.Summary.BuildIncludedFilesPerHoster();

        Assert.NotNull(map);
        HashSet<string> included = Assert.Contains("BRupload", map!);
        Assert.Equal(2, included.Count);                 // the auto-fit kept 2 of 3
        Assert.Contains("big.bin", included);            // FullPath of the kept big file
        Assert.DoesNotContain(droppedPath, included);    // the auto-dropped file is excluded
    }

    [Fact]
    public void Summary_BackFromStartStep_PreservesManualCheckboxEdits()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        // Plenty of free space so auto-fit unchecks nothing — any unchecked file is the user's edit.
        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 1_000_000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 100, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 200, IsSelected = true });
        brupload.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary built = Assert.Single(vm.Summary.Summaries);
        Assert.Equal(2, built.IncludedCount);

        // User manually unchecks one file on Page 3.
        built.Files.First(f => f.FileName == "a.bin").Included = false;
        Assert.Equal(1, built.IncludedCount);

        // Forward to the start-mode step, then Back to the summary.
        vm.CurrentStep = 3;
        vm.CurrentStep = 2;

        // Same summary instance, manual edit intact (no rebuild, no re-auto-fit).
        HosterUploadSummary afterBack = Assert.Single(vm.Summary.Summaries);
        Assert.Same(built, afterBack);
        Assert.Equal(1, afterBack.IncludedCount);
        Assert.False(afterBack.Files.First(f => f.FileName == "a.bin").Included);
    }

    [Fact]
    public void Summary_RebuildsWhenPage1SelectionChanges()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        FileEntry a = new() { FullPath = "a.bin", FileName = "a.bin", Size = 100, IsSelected = true };
        vm.Sources.Files.Add(a);
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 200, IsSelected = true });
        brupload.Use = true;
        vm.CurrentStep = 2;

        Assert.Equal(2, Assert.Single(vm.Summary.Summaries).IncludedCount);

        // Back, deselect a file on Page 1, return → the summary rebuilds with only the still-selected one.
        vm.CurrentStep = 1;
        a.IsSelected = false; // Page 1 change marks the summary dirty
        vm.CurrentStep = 2;

        SummaryFileItem only = Assert.Single(Assert.Single(vm.Summary.Summaries).Files);
        Assert.Equal("b.bin", only.FileName);
    }

    // ── Summary-step live storage refresh ──

    private static FileHosterSelectionViewModel IcerBoxRow(long quota, long used)
        => new("IcerBox", [new FileHosterLoginDto { Id = 1, FileHosterName = "IcerBox", Username = "u", StorageQuotaBytes = quota, StorageUsedBytes = used }]);

    private UploadWizardViewModel WizardWithVerifier(IAccountVerifier verifier)
    {
        // IcerBox's pipeline IS IStorageRefreshablePipeline (so the refresh gate opens) and declares no
        // per-file cap, so file sizes don't interfere with the capacity test.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.IcerBoxPipeline()]);
        return new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry, verifier);
    }

    [Fact]
    public async Task Summary_RefreshShrinksAvailable_ReFitsLiveWhenPristine()
    {
        Mock<IAccountVerifier> verifier = new();
        verifier.Setup(v => v.RefreshStorageAsync("IcerBox", It.IsAny<FileHosterLoginDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageUsage(900L, 1000L)); // fresh: 100 free (snapshot said 1000)

        UploadWizardViewModel vm = WizardWithVerifier(verifier.Object);
        FileHosterSelectionViewModel icer = IcerBoxRow(quota: 1000, used: 0);
        vm.Hosters.FileHosters.Add(icer);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 80, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 80, IsSelected = true });
        icer.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);

        await vm.Summary.PendingStorageRefresh!;

        Assert.Equal(100L, entry.AvailableBytes); // refreshed (snapshot said 1000)
        Assert.Equal(1, entry.IncludedCount);     // 80 + 80 > 100 → live re-fit keeps one
        Assert.False(entry.IsRefreshing);
    }

    [Fact]
    public async Task Summary_RefreshReturnsNull_KeepsSnapshot()
    {
        Mock<IAccountVerifier> verifier = new();
        verifier.Setup(v => v.RefreshStorageAsync("IcerBox", It.IsAny<FileHosterLoginDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StorageUsage?)null);

        UploadWizardViewModel vm = WizardWithVerifier(verifier.Object);
        FileHosterSelectionViewModel icer = IcerBoxRow(quota: 1000, used: 0);
        vm.Hosters.FileHosters.Add(icer);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 80, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 80, IsSelected = true });
        icer.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);

        await vm.Summary.PendingStorageRefresh!;

        Assert.Equal(1000L, entry.AvailableBytes); // snapshot kept
        Assert.Equal(2, entry.IncludedCount);
        Assert.False(entry.IsRefreshing);
    }

    [Fact]
    public async Task Summary_UserEditedBeforeRefresh_UpdatesAvailableButDoesNotReFit()
    {
        // Gate the refresh so the user can edit BEFORE it lands (a sync mock would land during step-2
        // entry, before any edit). The refresh completes only when we set the result.
        TaskCompletionSource<StorageUsage?> gate = new();
        Mock<IAccountVerifier> verifier = new();
        verifier.Setup(v => v.RefreshStorageAsync("IcerBox", It.IsAny<FileHosterLoginDto>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        UploadWizardViewModel vm = WizardWithVerifier(verifier.Object);
        FileHosterSelectionViewModel icer = IcerBoxRow(quota: 1000, used: 0);
        vm.Hosters.FileHosters.Add(icer);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 80, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 80, IsSelected = true });
        icer.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);

        // User unchecks BOTH while the refresh is still in flight. A re-fit would put one back (one 80
        // fits the fresh 100); because the user edited, the landing refresh must NOT re-fit.
        entry.Files.First().Included = false;
        entry.Files.Last().Included = false;
        Assert.True(entry.HasUserEdits);

        gate.SetResult(new StorageUsage(900L, 1000L)); // now the refresh lands (100 free)
        await vm.Summary.PendingStorageRefresh!;

        Assert.Equal(100L, entry.AvailableBytes); // available still updated…
        Assert.Equal(0, entry.IncludedCount);     // …but the user's selection is untouched (no re-fit)
    }

    [Fact]
    public async Task Summary_EditedThenRefreshShrinksBelowSelection_BlocksNext()
    {
        TaskCompletionSource<StorageUsage?> gate = new();
        Mock<IAccountVerifier> verifier = new();
        verifier.Setup(v => v.RefreshStorageAsync("IcerBox", It.IsAny<FileHosterLoginDto>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        UploadWizardViewModel vm = WizardWithVerifier(verifier.Object);
        FileHosterSelectionViewModel icer = IcerBoxRow(quota: 1000, used: 0);
        vm.Hosters.FileHosters.Add(icer);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 80, IsSelected = true });
        vm.Sources.Files.Add(new FileEntry { FullPath = "b.bin", FileName = "b.bin", Size = 80, IsSelected = true });
        icer.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.True(entry.IsRefreshing); // in flight (gate not yet set)

        // The user makes an edit (toggle off then back on) so HasUserEdits latches while both stay
        // checked (160 ≤ snapshot 1000 → fine for now).
        entry.Files.First().Included = false;
        entry.Files.First().Included = true;
        Assert.True(entry.HasUserEdits);
        Assert.True(vm.CanGoNext);

        // Refresh shrinks available to 100. User edited → NO re-fit → 160 > 100 → over capacity.
        gate.SetResult(new StorageUsage(900L, 1000L));
        await vm.Summary.PendingStorageRefresh!;

        Assert.False(entry.IsRefreshing);
        Assert.Equal(100L, entry.AvailableBytes);
        Assert.Equal(2, entry.IncludedCount);   // not re-fitted (both still checked)
        Assert.True(entry.IsOverCapacity);
        Assert.False(vm.CanGoNext);             // blocked by the fresh, smaller available
    }

    [Fact]
    public async Task Summary_NonRefreshableHoster_IsNeverRefreshed()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]); // not storage-refreshable
        Mock<IAccountVerifier> verifier = new();
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry, verifier.Object);

        FileHosterSelectionViewModel brupload = new(
            "BRupload",
            [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u", StorageQuotaBytes = 1000L, StorageUsedBytes = 0L }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "a.bin", FileName = "a.bin", Size = 80, IsSelected = true });
        brupload.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        await vm.Summary.PendingStorageRefresh!;

        Assert.False(entry.IsRefreshing);
        verifier.Verify(
            v => v.RefreshStorageAsync(It.IsAny<string>(), It.IsAny<FileHosterLoginDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Summary_HosterCountLimit_TruncatesFileListToTheCap()
    {
        // BRupload's MaxFilesPerPackage is 30; feed 32 selected files. The summary should
        // list the first 30, and the remaining 2 should be orphans (no other hoster can
        // pick them up in this fixture).
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        for (int i = 0; i < 32; i++)
        {
            vm.Sources.Files.Add(new FileEntry { FullPath = $"f{i}.bin", FileName = $"f{i}.bin", Size = 1024, IsSelected = true });
        }

        brupload.Use = true;
        vm.CurrentStep = 2;

        HosterUploadSummary entry = Assert.Single(vm.Summary.Summaries);
        Assert.Equal(30, entry.FileCount);
        Assert.Equal(2, vm.Summary.OrphanFilesCount);
    }

    [Fact]
    public void Summary_DisabledAccount_OmitsHosterEntirely()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterLoginDto disabledAccount = new() { Id = 1, FileHosterName = "BRupload", Username = "u", Disabled = true };
        FileHosterSelectionViewModel brupload = new("BRupload", [disabledAccount]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "f.bin", FileName = "f.bin", Size = 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 2;

        Assert.Empty(vm.Summary.Summaries);
        Assert.Single(vm.Summary.OrphanFiles);
    }

    [Fact]
    public void Summary_FailedAccount_OmitsHosterEntirely()
    {
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterLoginDto failedAccount = new() { Id = 1, FileHosterName = "BRupload", Username = "u" };
        failedAccount.SetCheckStatus(AccountCheckStatus.Failed, "Auth failed");
        FileHosterSelectionViewModel brupload = new("BRupload", [failedAccount]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "f.bin", FileName = "f.bin", Size = 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 2;

        Assert.Empty(vm.Summary.Summaries);
        Assert.Single(vm.Summary.OrphanFiles);
    }

    [Fact]
    public void Summary_CanGoNext_OnStep2_AlwaysTrue()
    {
        // Step 2 is the summary; we always allow Next even with orphans — the user
        // accepts the partial coverage and the orphans simply don't upload.
        DefaultFileHosterRegistry registry = new([new CSUploader.Upload.Pipeline.Hosters.BRuploadPipeline()]);
        UploadWizardViewModel vm = new(_packageManager, _loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), new AppSettings(), registry);

        FileHosterSelectionViewModel brupload = new("BRupload", [new FileHosterLoginDto { Id = 1, FileHosterName = "BRupload", Username = "u" }]);
        vm.Hosters.FileHosters.Add(brupload);
        vm.Sources.Files.Add(new FileEntry { FullPath = "big.iso", FileName = "big.iso", Size = 5L * 1024 * 1024 * 1024, IsSelected = true });

        brupload.Use = true;
        vm.CurrentStep = 2;

        Assert.True(vm.Summary.HasOrphanFiles);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void IsLastStep_OnSummary_IsFalse_OnStartStep_IsTrue()
    {
        UploadWizardViewModel vm = CreateVm(Mock.Of<IDialogService>());

        vm.CurrentStep = 2;
        Assert.False(vm.IsLastStep);

        vm.CurrentStep = 3;
        Assert.True(vm.IsLastStep);
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
