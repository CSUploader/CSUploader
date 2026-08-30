// <copyright file="UploadsViewModelSortTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
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

/// <summary>
/// The Uploads tab's hierarchical sort, at the ViewModel boundary the head drives.
/// <para>
/// The ordering rules themselves are pinned in <c>UploadRowOrderTests</c>; what is covered here is
/// the part that only exists once a live queue is attached — that a package ARRIVING while a sort
/// is active lands at its rank without the grid being rebuilt, that expanding a package puts its
/// files under it in rank, and that a reorder clears the sort without a stale clear being able to
/// wipe a newer one.
/// </para>
/// </summary>
public sealed class UploadsViewModelSortTests : IAsyncLifetime
{
    private const string Hoster = "Rapidgator";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly string _tempDir;
    private readonly List<UploadScheduler> _schedulers = [];
    private readonly List<PackageManager> _managers = [];
    private readonly List<UploadsViewModel> _vms = [];

    public UploadsViewModelSortTests()
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
        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-sort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (UploadsViewModel vm in _vms)
        {
            vm.Dispose();
        }

        foreach (UploadScheduler scheduler in _schedulers)
        {
            scheduler.Dispose();
        }

        foreach (PackageManager manager in _managers)
        {
            await manager.DrainPendingPersistenceAsync();
        }

        _connection.Dispose();
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ApplySort_RanksPackagesAndTheirFiles()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z-b.bin", "z-a.bin"));
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a-one.bin"));

        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        Assert.Equal(
            ["Alpha", "a-one.bin", "Zulu", "z-a.bin", "z-b.bin"],
            vm.VisibleRows.Select(DisplayName));
    }

    [Fact]
    public async Task ApplySort_Null_ReturnsToDefaultOrder()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z-b.bin", "z-a.bin"));
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a-one.bin"));
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        vm.ApplySort(null);

        Assert.Equal(
            ["Zulu", "z-b.bin", "z-a.bin", "Alpha", "a-one.bin"],
            vm.VisibleRows.Select(DisplayName));
        Assert.Null(vm.ActiveSort);
    }

    [Fact]
    public async Task ApplySort_ReplacesRowsInOneReset()
    {
        // A re-rank must not pass through an EMPTY collection on its way: the grid drops the
        // user's selection against the empty list and never gets it back.
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z.bin"));
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a.bin"));
        List<int> countsSeen = [];
        vm.VisibleRows.CollectionChanged += (_, _) => countsSeen.Add(vm.VisibleRows.Count);

        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        Assert.Equal([4], countsSeen);
    }

    [Fact]
    public async Task PackageAddedWhileSorted_LandsAtItsRank_WithItsFiles()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        await manager.AddPackageOnlyAsync(MakeOptions("Mike", "m.bin"));
        await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z.bin"));
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        // Arrives last but ranks first — and must bring its own file with it rather than being
        // dropped between some other package and that package's files.
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a.bin"));

        Assert.Equal(
            ["Alpha", "a.bin", "Mike", "m.bin", "Zulu", "z.bin"],
            vm.VisibleRows.Select(DisplayName));
    }

    [Fact]
    public async Task PackageAddedWhileSorted_RankingLast_GoesToTheEnd()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a.bin"));
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z.bin"));

        Assert.Equal(["Alpha", "a.bin", "Zulu", "z.bin"], vm.VisibleRows.Select(DisplayName));
    }

    [Fact]
    public async Task ExpandingWhileSorted_PutsFilesUnderTheirPackageInRank()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z-b.bin", "z-a.bin"));
        await manager.AddPackageOnlyAsync(MakeOptions("Alpha", "a.bin"));
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));

        package.IsExpanded = false;
        package.IsExpanded = true;

        Assert.Equal(
            ["Alpha", "a.bin", "Zulu", "z-a.bin", "z-b.bin"],
            vm.VisibleRows.Select(DisplayName));
    }

    [Fact]
    public async Task MoveWhileSorted_ClearsTheSortAndSaysSo()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z-b.bin", "z-a.bin"));
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));
        int cleared = 0;
        vm.SortCleared += (_, _) => cleared++;

        vm.SelectedRows = [package.First()];
        vm.MoveSelectedCommand.Execute("-1");

        Assert.Null(vm.ActiveSort);
        Assert.Equal(1, cleared);
        Assert.Equal(["Zulu", "z-b.bin", "z-a.bin"], vm.VisibleRows.Select(DisplayName));
    }

    [Fact]
    public async Task MoveWhileUnsorted_RaisesNothing()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack();
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z.bin"));
        int cleared = 0;
        vm.SortCleared += (_, _) => cleared++;

        vm.SelectedRows = [package.First()];
        vm.MoveSelectedCommand.Execute("-1");

        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task MoveClear_SupersededByANewerSort_DoesNotWipeIt()
    {
        // The clear is posted, because it can originate inside a cell-edit commit where mutating
        // rows immediately is unsafe. That gap is long enough for the user to click a header, and
        // a stale clear landing afterwards would silently throw the new sort away.
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(dispatcher);
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("Zulu", "z.bin"));
        dispatcher.RunPosted();
        vm.ApplySort(new UploadSort("Name", ListSortDirection.Ascending));
        vm.SelectedRows = [package.First()];
        vm.MoveSelectedCommand.Execute("-1");

        // The user re-sorts before the queued clear gets its turn.
        UploadSort newer = new("HosterDisplay", ListSortDirection.Descending);
        vm.ApplySort(newer);
        dispatcher.RunPosted();

        Assert.Equal(newer, vm.ActiveSort);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static string DisplayName(object row)
        => row switch
        {
            Package package => package.Name,
            PackageFile file => file.Name,
            _ => row.ToString() ?? "?",
        };

    private (PackageManager Manager, UploadsViewModel Vm) BuildStack(IUiDispatcher? dispatcher = null)
    {
        AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Never };
        DefaultFileHosterRegistry registry = new([]);
        UploadScheduler scheduler = new(settings, BuildAttemptRunner(registry), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _schedulers.Add(scheduler);

        PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);
        _managers.Add(manager);

        UploadsViewModel vm = new(
            manager, settings, Mock.Of<IDialogService>(), dispatcher ?? new InlineUiDispatcher(), Mock.Of<IClipboardService>());
        _vms.Add(vm);
        return (manager, vm);
    }

    private static AttemptRunner BuildAttemptRunner(DefaultFileHosterRegistry registry)
    {
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, factory.Object);
    }

    private PackageOptions MakeOptions(string title, params string[] fileNames)
    {
        List<string> paths = [];
        foreach (string name in fileNames)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [1]);
            paths.Add(path);
        }

        FileHosterClient hoster = new(Hoster, Protocol.Http);
        return new PackageOptions
        {
            Title = title,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            SelectedFiles = paths,
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = Hoster, IsAnonymous = true } } },
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
