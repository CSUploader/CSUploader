// <copyright file="UploadsViewModelRemoveTests.cs" company="CSUploader">
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

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Removal behaviour of the Uploads tab. Focus: removing a file that is its package's LAST file must
/// also drop the now-empty package row (regression — it used to leave an empty package behind).
/// Packages/files are built without a DbId so removal stays purely in-memory (no repo round-trips).
/// </summary>
public class UploadsViewModelRemoveTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    public UploadsViewModelRemoveTests()
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
    public async Task RemoveSelected_LastFileOfPackage_RemovesTheEmptyPackageToo()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile only = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        pkg.AddPackageFiles(new[] { only });

        UploadsViewModel vm = CreateVmShowing(pkg, only);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<object> { only });

        // The lone file AND its now-empty package are both gone from the grid.
        Assert.DoesNotContain(only, vm.VisibleRows);
        Assert.DoesNotContain(pkg, vm.VisibleRows);
        Assert.DoesNotContain(pkg, vm.Packages);
    }

    [Fact]
    public async Task RemoveSelected_OneOfSeveralFiles_KeepsPackageAndSiblings()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile a = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        PackageFile b = MakeFile(pkg, hoster, login, @"C:\d\b.bin");
        pkg.AddPackageFiles(new[] { a, b });

        UploadsViewModel vm = CreateVmShowing(pkg, a, b);

        await vm.RemoveSelectedCommand.ExecuteAsync(new List<object> { a });

        // Only the removed file disappears; the package (still has b) and the sibling stay.
        Assert.DoesNotContain(a, vm.VisibleRows);
        Assert.Contains(b, vm.VisibleRows);
        Assert.Contains(pkg, vm.VisibleRows);
        Assert.Contains(pkg, vm.Packages);
    }

    [Fact]
    public async Task RemoveAllCompleted_RemovesCompletedFilesAndFullyCompletedPackages_KeepsTheRest()
    {
        // Package A: fully completed → whole package goes. Package B: mixed → only its completed file goes.
        (Package a, FileHosterClient hosterA, FileHosterLoginDto loginA) = MakePackage();
        PackageFile a1 = MakeFile(a, hosterA, loginA, @"C:\d\a1.bin");
        PackageFile a2 = MakeFile(a, hosterA, loginA, @"C:\d\a2.bin");
        a.AddPackageFiles(new[] { a1, a2 });
        a1.State = FileState.Completed;
        a2.State = FileState.Completed;

        (Package b, FileHosterClient hosterB, FileHosterLoginDto loginB) = MakePackage();
        PackageFile bDone = MakeFile(b, hosterB, loginB, @"C:\d\b1.bin");
        PackageFile bQueued = MakeFile(b, hosterB, loginB, @"C:\d\b2.bin");
        b.AddPackageFiles(new[] { bDone, bQueued });
        bDone.State = FileState.Completed;
        bQueued.State = FileState.UploadQueued;

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        UploadsViewModel vm = CreateVmShowing(dialog, (a, new[] { a1, a2 }), (b, new[] { bDone, bQueued }));

        await vm.RemoveAllCompletedCommand.ExecuteAsync(null);

        // Fully-completed package A is gone entirely; B keeps its queued file (and stays listed).
        Assert.DoesNotContain(a, vm.Packages);
        Assert.DoesNotContain(a, vm.VisibleRows);
        Assert.DoesNotContain(a1, vm.VisibleRows);
        Assert.DoesNotContain(bDone, vm.VisibleRows);
        Assert.Contains(b, vm.Packages);
        Assert.Contains(b, vm.VisibleRows);
        Assert.Contains(bQueued, vm.VisibleRows);

        // The opt-out prompt used the dedicated key and carried the completed count (3).
        dialog.Verify(
            d => d.ShowOptOutConfirmationAsync(
                ConfirmationKeys.RemoveCompletedUploads,
                It.Is<string>(m => m.Contains('3')),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveAllCompleted_Declined_RemovesNothing()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile done = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        pkg.AddPackageFiles(new[] { done });
        done.State = FileState.Completed;

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        UploadsViewModel vm = CreateVmShowing(dialog, (pkg, new[] { done }));

        await vm.RemoveAllCompletedCommand.ExecuteAsync(null);

        Assert.Contains(pkg, vm.Packages);
        Assert.Contains(done, vm.VisibleRows);
    }

    [Fact]
    public async Task RemoveAllCompleted_NothingCompleted_IsASilentNoOp_WithoutPrompt()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile queued = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        pkg.AddPackageFiles(new[] { queued }); // stays Idle

        Mock<IDialogService> dialog = new();
        UploadsViewModel vm = CreateVmShowing(dialog, (pkg, new[] { queued }));

        await vm.RemoveAllCompletedCommand.ExecuteAsync(null);

        Assert.Contains(pkg, vm.Packages);
        Assert.Contains(queued, vm.VisibleRows);
        dialog.Verify(
            d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>Multi-package variant of <see cref="CreateVmShowing(Package, PackageFile[])"/> with an
    /// injectable dialog mock, for the Remove-All-Completed sweeps.</summary>
    private UploadsViewModel CreateVmShowing(Mock<IDialogService> dialog, params (Package Pkg, PackageFile[] Files)[] packages)
    {
        UploadsViewModel vm = new(_packageManager, new AppSettings(), dialog.Object, new InlineUiDispatcher(), Mock.Of<IClipboardService>());
        foreach ((Package pkg, PackageFile[] files) in packages)
        {
            vm.Packages.Add(pkg);
            vm.VisibleRows.Add(pkg);
            foreach (PackageFile f in files)
            {
                vm.VisibleRows.Add(f);
            }
        }

        return vm;
    }

    private UploadsViewModel CreateVmShowing(Package pkg, params PackageFile[] files)
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        UploadsViewModel vm = new(_packageManager, new AppSettings(), dialog.Object, new InlineUiDispatcher(), Mock.Of<IClipboardService>());
        // These tests build the Package directly and never route it through the manager, so
        // PackageManager.PackageAdded never fires — mirror the grid state the PackageAdded handler
        // would build so RemoveSelected operates on a populated, expanded package. (The real
        // Post-routed PackageAdded/FileCompleted/PackageCompleted paths are covered end-to-end in
        // UploadsViewModelPackageEventTests, which drives the manager for real.)
        vm.Packages.Add(pkg);
        vm.VisibleRows.Add(pkg);
        foreach (PackageFile f in files)
        {
            vm.VisibleRows.Add(f);
        }

        return vm;
    }

    private static (Package Package, FileHosterClient Hoster, FileHosterLoginDto Login) MakePackage()
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        };
        return (new Package(options), hoster, login);
    }

    private static PackageFile MakeFile(Package pkg, FileHosterClient hoster, FileHosterLoginDto login, string path)
        => new(pkg, path, hoster, login);

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
