// <copyright file="UploadWizardSourcesTests.cs" company="CSUploader">
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

/// <summary>
/// The wizard's first step builds ONE file list out of however many folders and files the user points
/// at. It used to have a Directory/Files mode where choosing a folder cleared whatever was already
/// there, so a package drawn from two places quietly lost the first one.
/// </summary>
public class UploadWizardSourcesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly UploadWizardViewModel _vm;
    private readonly string _root;

    public UploadWizardSourcesTests()
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
        FileHosterLoginRepository loginRepo = new(factory);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        PackageManager packageManager = new(
            settings,
            _scheduler,
            new UploadPackageRepository(factory),
            new UploadPackageFileRepository(factory),
            loginRepo,
            Mock.Of<IAppLogger>(),
            registry);

        _vm = new UploadWizardViewModel(packageManager, loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), settings);

        _root = Path.Combine(Path.GetTempPath(), "csu-wiz-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TwoFoldersBothLand_InOneList()
    {
        string a = MakeFolder("rips", "e01.mkv", "e02.mkv");
        string b = MakeFolder("artwork", "cover.jpg");

        _vm.Sources.AddDroppedPaths([a, b]);

        Assert.Equal(3, _vm.Sources.Files.Count);
        Assert.Equal(2, _vm.Sources.Sources.Count);
        Assert.Equal([2, 1], _vm.Sources.Sources.Select(s => s.FileCount));
    }

    [Fact]
    public void AFolderWalksItsSubdirectories_AndThePathColumnStaysRelativeToIt()
    {
        string a = MakeFolder("season", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(a, "subs"));
        File.WriteAllText(Path.Combine(a, "subs", "e01.srt"), "x");

        _vm.Sources.AddDroppedPaths([a]);

        Assert.Equal(2, _vm.Sources.Files.Count);
        Assert.Contains(_vm.Sources.Files, f => f.RelativePath == "e01.mkv");
        Assert.Contains(_vm.Sources.Files, f => f.RelativePath == Path.Combine("subs", "e01.srt"));
    }

    [Fact]
    public void TheSameFileFromTwoSources_IsListedOnce()
    {
        string a = MakeFolder("rips", "e01.mkv");
        string file = Path.Combine(a, "e01.mkv");

        _vm.Sources.AddDroppedPaths([a]);
        _vm.Sources.AddDroppedPaths([file]);   // the same file, this time picked individually

        Assert.Single(_vm.Sources.Files);

        // ...and it earns no source row of its own, because it contributed nothing.
        Assert.Single(_vm.Sources.Sources);
    }

    [Fact]
    public void TheSameFolderAddedTwice_AddsNothingAndNoSecondRow()
    {
        string a = MakeFolder("rips", "e01.mkv");

        _vm.Sources.AddDroppedPaths([a]);
        _vm.Sources.AddDroppedPaths([a]);

        Assert.Single(_vm.Sources.Files);
        Assert.Single(_vm.Sources.Sources);
    }

    [Fact]
    public void IdenticalRelativePathsFromDifferentFolders_AreToldApart()
    {
        // Two rips, each with its own "e01.mkv" - the Path column would say the same thing twice, so
        // the second is prefixed with the folder it came from.
        string a = MakeFolder("show-s01", "e01.mkv");
        string b = MakeFolder("show-s02", "e01.mkv");

        _vm.Sources.AddDroppedPaths([a, b]);

        Assert.Equal(2, _vm.Sources.Files.Count);
        Assert.Equal(2, _vm.Sources.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(_vm.Sources.Files, f => f.RelativePath == Path.Combine("show-s02", "e01.mkv"));
    }

    [Fact]
    public void RemovingASource_DropsItsFilesAndLeavesTheOthersAlone()
    {
        string a = MakeFolder("rips", "e01.mkv", "e02.mkv");
        string b = MakeFolder("artwork", "cover.jpg");
        _vm.Sources.AddDroppedPaths([a, b]);

        // Untick a survivor first: removing a DIFFERENT source must not disturb it.
        _vm.Sources.Files.First(f => f.FileName == "cover.jpg").IsSelected = false;

        _vm.Sources.RemoveSourceCommand.Execute(_vm.Sources.Sources.First(s => s.Path == a));

        Assert.Single(_vm.Sources.Files);
        Assert.Equal("cover.jpg", _vm.Sources.Files[0].FileName);
        Assert.False(_vm.Sources.Files[0].IsSelected);
        Assert.Single(_vm.Sources.Sources);
        Assert.True(_vm.Sources.HasSources);
    }

    [Fact]
    public void RemovingTheLastSource_EmptiesTheListButKeepsTheTypedTitle()
    {
        string a = MakeFolder("rips", "e01.mkv");
        _vm.Sources.AddDroppedPaths([a]);
        _vm.Sources.PackageTitle = "My own title";

        _vm.Sources.RemoveSourceCommand.Execute(_vm.Sources.Sources[0]);

        Assert.Empty(_vm.Sources.Files);
        Assert.Empty(_vm.Sources.Sources);
        Assert.False(_vm.Sources.HasSources);
        Assert.Equal("My own title", _vm.Sources.PackageTitle);
    }

    [Fact]
    public void TheTitleIsSeededFromTheFirstSourceOnly_AndNeverOverwrites()
    {
        string a = MakeFolder("Season 1", "e01.mkv");
        string b = MakeFolder("Extras", "bonus.mkv");

        _vm.Sources.AddDroppedPaths([a]);
        Assert.Equal("Season 1", _vm.Sources.PackageTitle);

        _vm.Sources.AddDroppedPaths([b]);
        Assert.Equal("Season 1", _vm.Sources.PackageTitle);
    }

    [Fact]
    public void ALooseFileSeedsTheTitleWithoutItsExtension()
    {
        string file = Path.Combine(_root, "one-off.mkv");
        File.WriteAllText(file, "x");

        _vm.Sources.AddDroppedPaths([file]);

        Assert.Equal("one-off", _vm.Sources.PackageTitle);
    }

    [Fact]
    public void DroppedPathsThatAreNeitherFileNorFolder_AreIgnored()
    {
        // A drop can carry all sorts of things; refusing the whole gesture over one of them helps
        // nobody, so the good paths still land.
        string a = MakeFolder("rips", "e01.mkv");

        _vm.Sources.AddDroppedPaths([a, Path.Combine(_root, "does-not-exist.bin"), "http://example.test/x"]);

        Assert.Single(_vm.Sources.Files);
        Assert.Single(_vm.Sources.Sources);
    }

    [Fact]
    public void EveryFileRemembersWhichSourceAddedIt()
    {
        string a = MakeFolder("rips", "e01.mkv");
        string b = MakeFolder("artwork", "cover.jpg");
        _vm.Sources.AddDroppedPaths([a, b]);

        UploadSource folderA = _vm.Sources.Sources.First(s => s.Path == a);
        Assert.All(
            _vm.Sources.Files.Where(f => f.FileName == "e01.mkv"),
            f => Assert.Equal(folderA.Id, f.SourceId));
        Assert.DoesNotContain(_vm.Sources.Files, f => f.SourceId == Guid.Empty);
    }

    private string MakeFolder(string name, params string[] files)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (string f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), f);
        }

        return dir;
    }

    /// <summary>A runner the scheduler can hold but never uses here - no upload runs in these tests.</summary>
    private static AttemptRunner BuildAttemptRunner()
    {
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> handlers = new();
        handlers.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(new DefaultFileHosterRegistry([]), proxy.Object, handlers.Object);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
