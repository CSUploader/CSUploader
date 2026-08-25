// <copyright file="UploadedViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
        DefaultFileHosterRegistry registry = new([]);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        _packageManager = new PackageManager(settings, _scheduler, _packageRepo, _fileRepo, loginRepo, logger, registry);
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
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

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
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

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
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

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

        dialog.Verify(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_AnonymousFile_AccountDisplayShowsLocalizedAnonymousLabel()
    {
        // FileHosterLoginId == 0 is the anonymous marker (no account row); the history row
        // must show the localized "(anonymous)" rather than the (null) stored account.
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "anon.bin", FileState.Completed, fileHosterLoginId: 0, fileHosterAccount: null);

        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        await vm.LoadAsync();
        UploadedFileRow row = vm.Files.Single(r => r.FileName == "anon.bin");

        Assert.Equal(Localizer.Instance["Wizard_Step2_AccountAnonymous"], row.AccountDisplay);
    }

    [Fact]
    public async Task LoadAsync_RegisteredAccountFile_AccountDisplayShowsPersistedAccount()
    {
        // A real account (FileHosterLoginId > 0) shows the denormalized account name, proving
        // FileHosterAccount round-trips through the repository persist + load path.
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "acct.bin", FileState.Completed, fileHosterLoginId: 7, fileHosterAccount: "bob@example.com");

        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        await vm.LoadAsync();
        UploadedFileRow row = vm.Files.Single(r => r.FileName == "acct.bin");

        Assert.Equal("bob@example.com", row.AccountDisplay);
    }

    [Fact]
    public void BuildColumnCopyText_MultipleSelectedRows_JoinsTheColumnValuePerRow()
    {
        // Regression: "Copy → URL" with several rows selected used to copy only the first.
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SelectedRows =
        [
            new UploadedFileRow { FileUrl = "https://h/a" },
            new UploadedFileRow { FileUrl = "https://h/b" },
            new UploadedFileRow { FileUrl = "https://h/c" },
        ];

        Assert.Equal(
            string.Join(Environment.NewLine, "https://h/a", "https://h/b", "https://h/c"),
            vm.BuildColumnCopyText("URL"));
    }

    [Fact]
    public void BuildColumnCopyText_NoMultiSelection_FallsBackToPrimarySelectedRow()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SelectedRow = new UploadedFileRow { FileUrl = "https://h/only" }; // SelectedRows left empty

        Assert.Equal("https://h/only", vm.BuildColumnCopyText("URL"));
    }

    [Fact]
    public void BuildColumnCopyText_SkipsRowsWithABlankValue()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SelectedRows =
        [
            new UploadedFileRow { FileUrl = "https://h/a" },
            new UploadedFileRow { FileUrl = string.Empty },
            new UploadedFileRow { FileUrl = "https://h/c" },
        ];

        Assert.Equal(
            string.Join(Environment.NewLine, "https://h/a", "https://h/c"),
            vm.BuildColumnCopyText("URL"));
    }

    [Fact]
    public void BuildColumnCopyText_NothingSelected_ReturnsNull()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());

        Assert.Null(vm.BuildColumnCopyText("URL"));
    }

    // ---- "Open URL" multi-select (operates on the whole grid selection) ----

    [Fact]
    public void SelectedDistinctUrls_ReturnsDistinctNonEmptyUrls_InSelectionOrder()
    {
        IReadOnlyList<string> urls = UploadedViewModel.SelectedDistinctUrls(new List<UploadedFileRow>
        {
            new() { FileUrl = "https://h/a" },
            new() { FileUrl = "https://h/b" },
            new() { FileUrl = "https://h/a" }, // duplicate URL folds away
            new() { FileUrl = string.Empty },  // blank contributes nothing
            new() { FileUrl = null },           // older entry with no URL
        });

        Assert.Equal(new[] { "https://h/a", "https://h/b" }, urls);
    }

    [Fact]
    public void SelectedDistinctUrls_NullSelection_ReturnsEmpty()
        => Assert.Empty(UploadedViewModel.SelectedDistinctUrls(null));

    [Fact]
    public void CanOpenUrl_TrueOnlyWhenSomeSelectedRowHasAUrl()
    {
        Assert.True(UploadedViewModel.CanOpenUrl(new List<UploadedFileRow>
        {
            new() { FileUrl = null },
            new() { FileUrl = "https://h/a" },
        }));
        Assert.False(UploadedViewModel.CanOpenUrl(new List<UploadedFileRow>
        {
            new() { FileUrl = null },
            new() { FileUrl = string.Empty },
        }));
        Assert.False(UploadedViewModel.CanOpenUrl(null));
    }

    [Fact]
    public async Task RequestRefresh_WhileTabHidden_DefersTheReload_UntilSetActiveDrainsIt()
    {
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SetActive(false); // user is parked on the Uploads tab during a run

        // A completion lands while the History tab is hidden — the full-table reload must NOT run.
        vm.RequestRefresh();
        await Task.Delay(100); // ample time for a wrongly-started reload against the in-memory DB
        Assert.Empty(vm.Files);

        // Showing the tab drains the pending request into one coalesced reload.
        vm.SetActive(true);
        for (int i = 0; i < 200 && vm.Files.Count == 0; i++)
        {
            await Task.Delay(10); // LoadAsync completes asynchronously after the inline Post
        }

        Assert.Single(vm.Files);
    }

    [Fact]
    public async Task RequestRefresh_WhileTabVisible_ReloadsAsBefore()
    {
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>()); // default state is active

        vm.RequestRefresh();
        for (int i = 0; i < 200 && vm.Files.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Single(vm.Files); // visible-tab behavior is unchanged: completions reload (coalesced)
    }

    [Fact]
    public async Task CopyLinks_FormatsSelectedRowsToClipboard_UnknownKeyOrNoLinksNoOps()
    {
        Mock<IClipboardService> clipboard = new();
        UploadedViewModel vm = new(
            _packageRepo, _fileRepo, _packageManager, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(),
            new InlineUiDispatcher(), clipboard.Object);
        vm.SelectedRows =
        [
            new UploadedFileRow { FileName = "a.r00", FileHosterName = "Rapidgator", FileUrl = "https://rg/a0" },
            new UploadedFileRow { FileName = "a.r00", FileHosterName = "KatFile", FileUrl = "https://kf/a0" },
            new UploadedFileRow { FileName = "x.bin", FileHosterName = "Rapidgator", FileUrl = null }, // skipped
        ];

        await vm.CopyLinksCommand.ExecuteAsync("ByHoster.Plain");

        string expected = string.Join(
            Environment.NewLine,
            "Rapidgator", "https://rg/a0", string.Empty, "KatFile", "https://kf/a0");
        clipboard.Verify(c => c.SetTextAsync(expected), Times.Once);

        // Unknown key and an all-URL-less selection both leave the clipboard untouched.
        await vm.CopyLinksCommand.ExecuteAsync("Bogus.Key");
        vm.SelectedRows = [new UploadedFileRow { FileName = "x", FileHosterName = "H", FileUrl = null }];
        await vm.CopyLinksCommand.ExecuteAsync("ByFile.Plain");
        clipboard.Verify(c => c.SetTextAsync(It.IsAny<string>()), Times.Once);
    }

    // ── Search: the predicate the History tab's grouped view applies ──

    private static UploadedFileRow SearchRow() => new()
    {
        FileName = "Holiday-Photos.zip",
        PackageName = "Summer 2026",
        FileHosterName = "VikingFile",
        FileUrl = "https://viking.example/f/abc123",
    };

    [Theory]
    [InlineData("holiday", true)]        // file name, case-insensitively
    [InlineData("PHOTOS.ZIP", true)]
    [InlineData("summer", true)]         // package name — a match keeps the whole group's promise
    [InlineData("viking", true)]         // hoster
    [InlineData("abc123", true)]         // URL only
    [InlineData("  holiday  ", true)]    // the needle is trimmed
    [InlineData("winter", false)]
    public void MatchesSearch_MatchesEveryFieldAUserRemembers(string search, bool expected)
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SearchText = search;

        Assert.Equal(expected, vm.MatchesSearch(SearchRow()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchesSearch_BlankSearch_MatchesEverything(string search)
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SearchText = search;

        Assert.True(vm.MatchesSearch(SearchRow()));
        Assert.True(vm.MatchesSearch(new object())); // even a non-row: a blank search filters nothing
    }

    [Fact]
    public void MatchesSearch_NullUrl_IsSafeAndJustDoesNotMatch()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SearchText = "abc123";
        UploadedFileRow row = SearchRow();
        row.FileUrl = null;

        Assert.False(vm.MatchesSearch(row));
    }

    [Fact]
    public void MatchesSearch_ANonRowItem_NeverMatchesAnActiveSearch()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        vm.SearchText = "anything";

        Assert.False(vm.MatchesSearch(new object()));
    }

    /// <summary>Editing the text is the only site where the result can change, so it is the one
    /// site that must tell the head to re-filter.</summary>
    [Fact]
    public void EditingSearchText_RaisesSearchInvalidated()
    {
        UploadedViewModel vm = CreateVm(Mock.Of<IDialogService>());
        int raised = 0;
        vm.SearchInvalidated += (_, _) => raised++;

        vm.SearchText = "a";
        vm.SearchText = "ab";
        vm.SearchText = "ab"; // unchanged — the generated setter does not re-raise

        Assert.Equal(2, raised);
    }

    private UploadedViewModel CreateVm(IDialogService dialogService) =>
        new(_packageRepo, _fileRepo, _packageManager, dialogService, Mock.Of<IAppLogger>(), new InlineUiDispatcher(), Mock.Of<IClipboardService>());

    private async Task<int> InsertPackageAsync(string name)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = false,
        };
        await _packageRepo.InsertAsync(pkg);
        return pkg.Id;
    }

    private async Task<int> InsertFileAsync(int packageId, string fileName, FileState state, int fileHosterLoginId = 0, string? fileHosterAccount = null)
    {
        UploadPackageFileDto file = new()
        {
            FileName = fileName,
            FileDirectory = "C:\\test",
            FileSize = 1024,
            FileHoster = "Rapidgator",
            FileHosterName = "Rapidgator",
            FileHosterLoginId = fileHosterLoginId,
            FileHosterAccount = fileHosterAccount,
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
