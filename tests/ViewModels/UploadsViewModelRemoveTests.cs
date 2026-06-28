// <copyright file="UploadsViewModelRemoveTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Generic;
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
    public void RemoveSelected_LastFileOfPackage_RemovesTheEmptyPackageToo()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile only = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        pkg.AddPackageFiles(new[] { only });

        UploadsViewModel vm = CreateVmShowing(pkg, only);

        vm.RemoveSelectedCommand.Execute(new List<object> { only });

        // The lone file AND its now-empty package are both gone from the grid.
        Assert.DoesNotContain(only, vm.VisibleRows);
        Assert.DoesNotContain(pkg, vm.VisibleRows);
        Assert.DoesNotContain(pkg, vm.Packages);
    }

    [Fact]
    public void RemoveSelected_OneOfSeveralFiles_KeepsPackageAndSiblings()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage();
        PackageFile a = MakeFile(pkg, hoster, login, @"C:\d\a.bin");
        PackageFile b = MakeFile(pkg, hoster, login, @"C:\d\b.bin");
        pkg.AddPackageFiles(new[] { a, b });

        UploadsViewModel vm = CreateVmShowing(pkg, a, b);

        vm.RemoveSelectedCommand.Execute(new List<object> { a });

        // Only the removed file disappears; the package (still has b) and the sibling stay.
        Assert.DoesNotContain(a, vm.VisibleRows);
        Assert.Contains(b, vm.VisibleRows);
        Assert.Contains(pkg, vm.VisibleRows);
        Assert.Contains(pkg, vm.Packages);
    }

    private UploadsViewModel CreateVmShowing(Package pkg, params PackageFile[] files)
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        UploadsViewModel vm = new(_packageManager, new AppSettings(), dialog.Object);
        // Mirror the grid state the PackageAdded handler would build (its Dispatcher.BeginInvoke is a
        // no-op in a headless test), so RemoveSelected operates on a populated, expanded package.
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
