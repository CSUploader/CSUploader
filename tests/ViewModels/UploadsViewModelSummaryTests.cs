// <copyright file="UploadsViewModelSummaryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
using Xunit;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Pins the Upload Overview footer's per-tick aggregation: the ~14 independent full-queue getter scans were
/// collapsed into a single cached pass (<c>RecomputeSummary</c> → <see cref="Package.ComputeAggregate"/>),
/// so this asserts the cached getters reflect the whole queue after a tick and — matching the prior
/// behaviour — only refresh on that tick.
/// </summary>
public sealed class UploadsViewModelSummaryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    public UploadsViewModelSummaryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        TestDbContextFactory factory = new(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        AppSettings settings = new();
        DefaultFileHosterRegistry registry = new([]);
        UploadPackageFileRepository fileRepo = new(factory);
        UploadPackageRepository packageRepo = new(factory);
        FileHosterLoginRepository loginRepo = new(factory);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _packageManager = new PackageManager(settings, _scheduler, packageRepo, fileRepo, loginRepo, Mock.Of<IAppLogger>(), registry);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Tick_RefreshesCachedFooterAggregatesAcrossAllPackages()
    {
        InlineUiDispatcher dispatcher = new();
        UploadsViewModel vm = new(_packageManager, new AppSettings(), Mock.Of<IDialogService>(), dispatcher, Mock.Of<IClipboardService>());

        Package p1 = MakePackage("P1");
        p1.AddPackageFiles(
        [
            MakeFile(p1, size: 1000, loaded: 400, remaining: 600, speed: 50, FileState.Uploading),
            MakeFile(p1, size: 2000, loaded: 2000, remaining: null, speed: null, FileState.Completed),
        ]);
        Package p2 = MakePackage("P2");
        p2.AddPackageFiles([MakeFile(p2, size: 500, loaded: null, remaining: 500, speed: null, FileState.Failed)]);
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);

        // Before a tick the cached footer is still empty — the footer only refreshes on the tick, exactly
        // as it did when the getters recomputed live (they were only re-read when the tick raised PropertyChanged).
        Assert.Equal(0, vm.RunningUploads);
        Assert.Equal(0, vm.FileCount);

        dispatcher.Timers[0].Tick();

        Assert.Equal(3, vm.FileCount);
        Assert.Equal(1, vm.RunningUploads);
        Assert.Equal(1, vm.FinishedLinks);
        Assert.Equal(1, vm.FailedLinks);
        Assert.Equal(50, vm.CurrentSpeedBytesPerSecond);
        Assert.Equal(ByteUnit.FromBytes(3500, ByteBase.Binary).ToFriendlyString(), vm.TotalBytes);   // 1000+2000+500
        Assert.Equal(ByteUnit.FromBytes(2400, ByteBase.Binary).ToFriendlyString(), vm.BytesLoaded);  // 400+2000
        Assert.Equal(ByteUnit.FromBytes(1100, ByteBase.Binary).ToFriendlyString(), vm.RemainingBytes); // 600+500
    }

    [Fact]
    public void ExpandCollapse_LargePackage_KeepsVisibleRowsCorrect_ViaTheBulkResetPath()
    {
        // A package big enough for the RangeObservableCollection Reset regime — pins that the bulk
        // insert/remove path (single Reset instead of per-row events) yields exactly the same rows the
        // old per-item path produced: package row followed by its files in order, then clean removal.
        InlineUiDispatcher dispatcher = new();
        UploadsViewModel vm = new(_packageManager, new AppSettings(), Mock.Of<IDialogService>(), dispatcher, Mock.Of<IClipboardService>());

        const int FileCount = RangeObservableCollection<object>.RangeThreshold + 10;
        Package big = MakePackage("Big");
        PackageFile[] files = [.. Enumerable.Range(0, FileCount)
            .Select(i => MakeFile(big, size: 100, loaded: null, remaining: 100, speed: null, FileState.UploadQueued))];
        big.AddPackageFiles(files);

        vm.Packages.Add(big);
        vm.VisibleRows.Add(big);
        vm.VisibleRows.InsertRange(1, [.. files]);
        Assert.Equal(FileCount + 1, vm.VisibleRows.Count);
        Assert.Same(big, vm.VisibleRows[0]);
        Assert.Same(files[0], vm.VisibleRows[1]);            // files follow the package row, in order
        Assert.Same(files[^1], vm.VisibleRows[FileCount]);

        vm.VisibleRows.RemoveRange([.. files]);              // collapse
        Assert.Equal([big], vm.VisibleRows);                 // only the package row survives

        vm.VisibleRows.InsertRange(1, [.. files]);           // re-expand restores the identical layout
        Assert.Equal(FileCount + 1, vm.VisibleRows.Count);
        Assert.Same(files[0], vm.VisibleRows[1]);
    }

    [Fact]
    public void Tick_IsSkippedWhileInactive_AndSetActiveTrue_RunsAnImmediateCatchUpRefresh()
    {
        InlineUiDispatcher dispatcher = new();
        UploadsViewModel vm = new(_packageManager, new AppSettings(), Mock.Of<IDialogService>(), dispatcher, Mock.Of<IClipboardService>());
        Package p = MakePackage("P");
        p.AddPackageFiles([MakeFile(p, size: 1000, loaded: 400, remaining: 600, speed: 50, FileState.Uploading)]);
        vm.Packages.Add(p);

        vm.SetActive(false); // user switched to another tab
        dispatcher.Timers[0].Tick();
        Assert.Equal(0, vm.RunningUploads); // tick did no work while inactive — cache stays empty

        vm.SetActive(true); // returning to the Uploads tab refreshes immediately, without waiting for a tick
        Assert.Equal(1, vm.RunningUploads);
        Assert.Equal(50, vm.CurrentSpeedBytesPerSecond);
    }

    private static Package MakePackage(string name)
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        return new Package(new PackageOptions
        {
            Title = name,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        });
    }

    private static PackageFile MakeFile(Package pkg, long? size, long? loaded, long? remaining, long? speed, FileState state)
    {
        FileHosterClient hoster = pkg.FileHosterLogins.Keys.First();
        FileHosterLoginDto login = pkg.FileHosterLogins.Values.First();
        return new PackageFile(pkg, @"C:\d\f.bin", hoster, login)
        {
            Size = size,
            BytesLoaded = loaded,
            BytesRemaining = remaining,
            Speed = speed,
            State = state,
        };
    }

    private static AttemptRunner BuildAttemptRunner()
    {
        DefaultFileHosterRegistry registry = new([]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
