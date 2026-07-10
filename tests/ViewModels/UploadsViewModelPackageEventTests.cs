// <copyright file="UploadsViewModelPackageEventTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
/// Covers the Uploads tab's Post-routed event handlers — the exact paths the Avalonia head will drive
/// through a real dispatcher. Uses <see cref="InlineUiDispatcher"/> so every <c>_uiDispatcher.Post</c>
/// runs inline, then drives a REAL <see cref="PackageManager"/> + <see cref="UploadScheduler"/> (with a
/// pipeline that completes instantly) so the manager raises <c>PackageAdded</c> / <c>FileCompleted</c> /
/// <c>PackageCompleted</c> through production code rather than a synthetic re-raise. Also pins the two
/// distinct VisibleRows-affecting routes apart: FilterText edits raise <c>FilterInvalidated</c> without
/// touching VisibleRows, while an IsExpanded toggle mutates VisibleRows without raising it.
/// </summary>
// IAsyncLifetime so DisposeAsync can drain PackageManager's fire-and-forget persistence before the
// shared SqliteConnection closes (see tests/CLAUDE.md; same rationale as PackageManagerSoftRemoveTests).
public sealed class UploadsViewModelPackageEventTests : IAsyncLifetime
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

    public UploadsViewModelPackageEventTests()
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-pkgevt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (UploadsViewModel vm in _vms)
        {
            vm.Dispose();
        }

        // Stop the source of new FileStateChanged events before draining, then let each manager's
        // in-flight persistence finish so no EF Core write races the connection dispose.
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
    public async Task PackageAdded_BuildsVisibleRow()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("added", "a.bin"));

        // AddPackageOnlyAsync raises PackageAdded synchronously; the VM's Post-routed handler runs
        // inline, so the package row — and its file row, since packages default to expanded — land in
        // VisibleRows with no pumping.
        PackageFile file = package.Single();
        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(file, vm.VisibleRows);
        Assert.Contains(package, vm.Packages);
    }

    [Fact]
    public async Task FileCompleted_ImmediatelyMode_PrunesRowAndEmptyPackage()
    {
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, new CompletingPipeline(Hoster));

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("done", "a.bin"));
        PackageFile file = package.Single();
        Assert.Contains(file, vm.VisibleRows); // built by PackageAdded, before the upload runs

        manager.SchedulePackage(package);

        // The pipeline drives the file to Completed; the manager raises FileCompleted, whose VM handler
        // (Immediately mode) posts RemoveFileAndPruneEmptyPackage inline — the file row and its now-empty
        // package both vanish.
        await WaitForDrained(manager, () => !vm.VisibleRows.Contains(file) && !vm.VisibleRows.Contains(package));

        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.Packages);
    }

    [Fact]
    public async Task PackageCompleted_WhenPackageIsReadyMode_RemovesPackage()
    {
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.WhenPackageIsReady };
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, new CompletingPipeline(Hoster));

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("ready", "a.bin"));
        PackageFile file = package.Single();
        Assert.Contains(package, vm.VisibleRows);

        manager.SchedulePackage(package);

        // WhenPackageIsReady: the per-file FileCompleted handler no-ops; only PackageCompleted (raised
        // once every file succeeded) removes the package via the inline Post.
        await WaitForDrained(manager, () => !vm.VisibleRows.Contains(package));

        Assert.DoesNotContain(package, vm.VisibleRows);
        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.Packages);
    }

    [Fact]
    public async Task IsExpandedToggle_AddsAndRemovesFileRows_WithoutRaisingFilterInvalidated()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("toggle", "a.bin"));
        PackageFile file = package.Single();

        // Package defaults to expanded, so the file row is present after PackageAdded.
        Assert.Contains(file, vm.VisibleRows);

        int filterInvalidated = 0;
        vm.FilterInvalidated += (_, _) => filterInvalidated++;

        // Collapse: Package_PropertyChanged posts RemovePackageFiles (inline) — the file row disappears.
        // This route deliberately raises NO FilterInvalidated (that was the dead RebuildVisibleRows path).
        package.IsExpanded = false;
        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.Contains(package, vm.VisibleRows);

        // Expand again: the file row reappears.
        package.IsExpanded = true;
        Assert.Contains(file, vm.VisibleRows);

        Assert.Equal(0, filterInvalidated);
    }

    [Fact]
    public async Task FilterTextChange_RaisesFilterInvalidated_ButLeavesVisibleRowsUntouched()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());
        await manager.AddPackageOnlyAsync(MakeOptions("filter", "a.bin"));
        int before = vm.VisibleRows.Count;

        int filterInvalidated = 0;
        vm.FilterInvalidated += (_, _) => filterInvalidated++;

        // The OTHER route: editing FilterText raises FilterInvalidated (each head re-runs its own view
        // filter) but never mutates the VM's VisibleRows itself.
        vm.FilterText = "abc";

        Assert.Equal(1, filterInvalidated);
        Assert.Equal(before, vm.VisibleRows.Count);
    }

    private (PackageManager Manager, UploadsViewModel Vm) BuildStack(AppSettings settings, IFileHosterPipeline? pipeline = null)
    {
        DefaultFileHosterRegistry registry = new(pipeline is null ? [] : [pipeline]);
        UploadScheduler scheduler = new(settings, BuildAttemptRunner(registry), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _schedulers.Add(scheduler);

        PackageManager manager = new(settings, scheduler, _packageRepo, _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);
        _managers.Add(manager);

        UploadsViewModel vm = new(manager, settings, Mock.Of<IDialogService>(), new InlineUiDispatcher(), Mock.Of<IClipboardService>());
        _vms.Add(vm);

        return (manager, vm);
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

    /// <summary>
    /// Polls <paramref name="condition"/> to a timeout, draining the manager's fire-and-forget
    /// persistence before each check — the FileCompleted/PackageCompleted events (and thus the VM's
    /// inline prune) fire from inside that persistence callback, so draining is what makes the outcome
    /// observable, and it leaves the collection settled for the assertion.
    /// </summary>
    private static async Task WaitForDrained(PackageManager manager, Func<bool> condition, int timeoutMs = 5000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            await manager.DrainPendingPersistenceAsync();
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
            waited += 20;
        }

        await manager.DrainPendingPersistenceAsync();
        Assert.True(condition(), "condition was not met within the timeout");
    }

    private static AttemptRunner BuildAttemptRunner(IFileHosterRegistry registry)
    {
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    /// <summary>
    /// Test pipeline whose upload completes immediately — drives a file straight to
    /// <see cref="FileState.Completed"/> through the real scheduler so the manager raises the
    /// FileCompleted/PackageCompleted events the VM's Post-routed handlers consume.
    /// </summary>
    private sealed class CompletingPipeline(string name) : IFileHosterPipeline
    {
        public string Name { get; } = name;

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "ok"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            yield return new TransferCompleted("https://done/" + ctx.FileName);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
