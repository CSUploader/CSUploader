// <copyright file="UploadWizardTreeTests.cs" company="CSUploader">
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
/// The wizard's source tree. It exists for two reasons, and both are asserted here: a flat list can't
/// say what a package is made of once it draws from several places, and the strip that used to list
/// the sources sat ABOVE the grid, so every folder added cost the file list height.
/// </summary>
public class UploadWizardTreeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly UploadWizardViewModel _vm;
    private readonly string _root;

    public UploadWizardTreeTests()
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

        _root = Path.Combine(Path.GetTempPath(), "csu-tree-" + Path.GetRandomFileName());
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
    public void TheTreeMirrorsTheRealSubdirectoryStructure()
    {
        string season = MakeFolder("Season 1", "e01.mkv", "e02.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs", "eng"));
        File.WriteAllText(Path.Combine(season, "subs", "eng", "e01.srt"), "s");

        _vm.AddDroppedPaths([season]);

        UploadTreeNode all = Assert.Single(_vm.TreeRoots);
        UploadTreeNode root = Assert.Single(all.Children);
        Assert.Equal("Season 1", root.Name);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(2, root.OwnFiles.Count);          // the two mkvs sit directly in it

        UploadTreeNode subs = Assert.Single(root.Children);
        Assert.Equal("subs", subs.Name);
        UploadTreeNode eng = Assert.Single(subs.Children);
        Assert.Equal("eng", eng.Name);
        Assert.Single(eng.OwnFiles);
    }

    [Fact]
    public void IndividuallyPickedFilesShareOneBucket_RatherThanANodeEach()
    {
        // A node per picked file would be the flat list again, with more indentation.
        string a = Path.Combine(_root, "one.nfo");
        string b = Path.Combine(_root, "two.nfo");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");

        _vm.AddDroppedPaths([a, b]);

        UploadTreeNode all = Assert.Single(_vm.TreeRoots);
        UploadTreeNode loose = Assert.Single(all.Children);
        Assert.Equal(UploadTreeNodeKind.LooseFiles, loose.Kind);
        Assert.Equal(2, loose.FileCount);
    }

    [Fact]
    public void SelectingANode_ScopesTheGridToItAndEverythingBeneath()
    {
        string season = MakeFolder("Season 1", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs"));
        File.WriteAllText(Path.Combine(season, "subs", "e01.srt"), "s");
        string other = MakeFolder("artwork", "cover.jpg");

        _vm.AddDroppedPaths([season, other]);

        UploadTreeNode all = _vm.TreeRoots[0];
        UploadTreeNode seasonNode = all.Children.First(c => c.Name == "Season 1");

        _vm.SelectedNode = seasonNode;
        Assert.Equal(2, Visible().Length);                       // the mkv AND the srt beneath it
        Assert.DoesNotContain("cover.jpg", Visible());

        _vm.SelectedNode = seasonNode.Children.First(c => c.Name == "subs");
        Assert.Equal(["e01.srt"], Visible());

        _vm.SelectedNode = all;
        Assert.Equal(3, Visible().Length);
    }

    [Fact]
    public void TheTextFilterAndTheTreeSelectionNarrowTogether()
    {
        // Both hide rows through the same flag, so the one that runs last must not undo the other.
        string season = MakeFolder("Season 1", "e01.mkv", "e01.nfo");
        MakeFolder("artwork", "cover.jpg");
        _vm.AddDroppedPaths([season, Path.Combine(_root, "artwork")]);

        _vm.SelectedNode = _vm.TreeRoots[0].Children.First(c => c.Name == "Season 1");
        _vm.FileFilter = "nfo";

        Assert.Equal(["e01.nfo"], Visible());
    }

    [Fact]
    public void TickingAFolder_TicksEverythingBeneathIt()
    {
        string season = MakeFolder("Season 1", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs"));
        File.WriteAllText(Path.Combine(season, "subs", "e01.srt"), "s");
        _vm.AddDroppedPaths([season]);

        UploadTreeNode seasonNode = _vm.TreeRoots[0].Children[0];
        Assert.True(seasonNode.IsChecked);   // everything arrives ticked

        seasonNode.IsChecked = false;
        Assert.All(_vm.Files, f => Assert.False(f.IsSelected));

        seasonNode.IsChecked = true;
        Assert.All(_vm.Files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public void APartiallyTickedBranch_ReadsAsIndeterminateAllTheWayUp()
    {
        string season = MakeFolder("Season 1", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs"));
        File.WriteAllText(Path.Combine(season, "subs", "e01.srt"), "s");
        _vm.AddDroppedPaths([season]);

        UploadTreeNode all = _vm.TreeRoots[0];
        UploadTreeNode seasonNode = all.Children[0];
        UploadTreeNode subs = seasonNode.Children[0];

        _vm.Files.First(f => f.FileName == "e01.srt").IsSelected = false;

        Assert.False(subs.IsChecked);       // that branch is now entirely unticked
        Assert.Null(seasonNode.IsChecked);  // …its parent is mixed
        Assert.Null(all.IsChecked);
    }

    [Fact]
    public void SettingIndeterminateOnAFolder_IsIgnored()
    {
        // A three-state box cycles checked -> unchecked -> indeterminate, and "make this branch
        // partially ticked" means nothing; accepting it would look like an action and do nothing.
        string season = MakeFolder("Season 1", "e01.mkv", "e02.mkv");
        _vm.AddDroppedPaths([season]);

        UploadTreeNode node = _vm.TreeRoots[0].Children[0];
        node.IsChecked = null;

        Assert.All(_vm.Files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public void SelectNone_LeavesEveryNodeUnticked_AndSelectAllPutsThemBack()
    {
        string season = MakeFolder("Season 1", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs"));
        File.WriteAllText(Path.Combine(season, "subs", "e01.srt"), "s");
        _vm.AddDroppedPaths([season]);

        UploadTreeNode all = _vm.TreeRoots[0];

        _vm.SelectNoneCommand.Execute(null);
        Assert.False(all.IsChecked);
        Assert.False(all.Children[0].IsChecked);

        _vm.SelectAllCommand.Execute(null);
        Assert.True(all.IsChecked);
        Assert.True(all.Children[0].IsChecked);
    }

    [Fact]
    public void AddingASecondFolder_KeepsTheSelectedSourceSelected()
    {
        string a = MakeFolder("first", "a.mkv");
        _vm.AddDroppedPaths([a]);
        UploadTreeNode firstNode = _vm.TreeRoots[0].Children[0];
        _vm.SelectedNode = firstNode;

        _vm.AddDroppedPaths([MakeFolder("second", "b.mkv")]);

        // The tree is rebuilt wholesale, so the node object differs — what must survive is WHERE the
        // user was, not which instance they were on.
        Assert.NotNull(_vm.SelectedNode);
        Assert.Equal("first", _vm.SelectedNode!.Name);
        Assert.Equal(["a.mkv"], Visible());
    }

    [Fact]
    public void RemovingASource_DropsItsBranch_AndFallsBackToTheAllNode()
    {
        string a = MakeFolder("first", "a.mkv");
        string b = MakeFolder("second", "b.mkv");
        _vm.AddDroppedPaths([a, b]);

        UploadTreeNode firstNode = _vm.TreeRoots[0].Children.First(c => c.Name == "first");
        _vm.SelectedNode = firstNode;

        _vm.RemoveSourceCommand.Execute(firstNode.Source);

        UploadTreeNode all = Assert.Single(_vm.TreeRoots);
        Assert.Single(all.Children);
        Assert.Equal("second", all.Children[0].Name);

        // The node that was selected is gone; the grid must not be left showing nothing.
        Assert.Equal(UploadTreeNodeKind.All, _vm.SelectedNode!.Kind);
        Assert.Equal(["b.mkv"], Visible());
    }

    [Fact]
    public void OnlyASourceRootOffersRemoval()
    {
        string season = MakeFolder("Season 1", "e01.mkv");
        Directory.CreateDirectory(Path.Combine(season, "subs"));
        File.WriteAllText(Path.Combine(season, "subs", "e01.srt"), "s");
        _vm.AddDroppedPaths([season]);

        UploadTreeNode all = _vm.TreeRoots[0];
        UploadTreeNode root = all.Children[0];

        Assert.True(root.IsRemovable);
        Assert.False(all.IsRemovable);
        Assert.False(root.Children[0].IsRemovable);   // an inner folder has no separate existence
    }

    private string[] Visible() => [.. _vm.Files.Where(f => f.IsVisible).Select(f => f.FileName)];

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
