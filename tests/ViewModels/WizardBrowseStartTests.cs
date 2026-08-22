// <copyright file="WizardBrowseStartTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Where the wizard's "Add files…" / "Add folder…" pickers open. Before this the folder picker
/// started at the last folder added in THAT wizard session and the file picker started nowhere at
/// all — <c>BrowseFilesAsync</c> had no start-directory parameter to give it one — so every fresh
/// wizard began wherever the OS felt like, however many times the user had just browsed elsewhere.
/// </summary>
public class WizardBrowseStartTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly string _root;

    public WizardBrowseStartTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _root = Path.Combine(Path.GetTempPath(), "csu-browse-start-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir that would not delete is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    // ── Which directory the picker is told to open in ──

    [Fact]
    public void SystemDefault_SuggestsNothing_AndLetsTheOsDecide()
    {
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            BrowseStartMode = BrowseStartMode.SystemDefault,
            BrowseStartFolder = _root,
            LastBrowsedFolder = _root,
        });

        // Both other values are populated on purpose: this mode must IGNORE them, not merely lack them.
        Assert.Null(vm.ResolveBrowseStart());
    }

    [Fact]
    public void FixedFolder_SuggestsTheConfiguredFolder_EvenWhenSomethingElseWasUsedLast()
    {
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            BrowseStartMode = BrowseStartMode.FixedFolder,
            BrowseStartFolder = _root,
            LastBrowsedFolder = Path.Combine(_root, "somewhere-else"),
        });

        Assert.Equal(_root, vm.ResolveBrowseStart());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FixedFolder_WithNothingConfigured_SuggestsNothing(string configured)
    {
        // The mode is selected but the box is empty — suggest nothing rather than a blank path the
        // picker would have to interpret.
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            BrowseStartMode = BrowseStartMode.FixedFolder,
            BrowseStartFolder = configured,
        });

        Assert.Null(vm.ResolveBrowseStart());
    }

    [Fact]
    public void LastUsed_SuggestsTheRememberedFolder()
    {
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            BrowseStartMode = BrowseStartMode.LastUsed,
            LastBrowsedFolder = _root,
        });

        Assert.Equal(_root, vm.ResolveBrowseStart());
    }

    [Fact]
    public void LastUsed_WithNothingRemembered_IsNoWorseThanBeforeTheSettingExisted()
    {
        // The pre-setting fallback: the last folder added in this wizard. A first-ever run has
        // neither, and then there is genuinely nothing to suggest.
        WizardSourcesViewModel vm = Build(new AppSettings { BrowseStartMode = BrowseStartMode.LastUsed });
        Assert.Null(vm.ResolveBrowseStart());

        string folder = Path.Combine(_root, "season-1");
        Directory.CreateDirectory(folder);
        vm.AddDroppedPaths([folder]);

        Assert.Equal(folder, vm.ResolveBrowseStart());
    }

    [Fact]
    public void WithNoSettingsAtAll_TheDefaultModeStillApplies()
    {
        // Null settings is the shape older callers and tests construct; it must not throw.
        WizardSourcesViewModel vm = Build(settings: null);

        Assert.Null(vm.ResolveBrowseStart());
    }

    // ── What gets remembered after a pick ──

    [Fact]
    public async Task PickingFiles_RemembersTheDirectoryTheyCameFrom()
    {
        string folder = Path.Combine(_root, "release");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.bin");
        await File.WriteAllBytesAsync(file, [1]);

        AppSettings settings = new() { BrowseStartMode = BrowseStartMode.LastUsed };
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync([file]);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object);
        await vm.AddFilesCommand.ExecuteAsync(null);

        Assert.Equal(folder, settings.LastBrowsedFolder);
    }

    [Fact]
    public async Task PickingAFolder_RemembersItsPARENT_SoTheNextOneIsOneClickAway()
    {
        // The whole point of remembering a DIRECTORY rather than the selection: reopening one level
        // up shows the folder just picked AND its siblings — the next season, the next release.
        string parent = Path.Combine(_root, "shows");
        string picked = Path.Combine(parent, "season-1");
        Directory.CreateDirectory(picked);

        AppSettings settings = new() { BrowseStartMode = BrowseStartMode.LastUsed };
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync([picked]);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object);
        await vm.AddFoldersCommand.ExecuteAsync(null);

        Assert.Equal(parent, settings.LastBrowsedFolder);
    }

    [Fact]
    public async Task TheOtherModes_RememberNothing_BecauseNeitherWouldEverReadIt()
    {
        string folder = Path.Combine(_root, "fixed");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.bin");
        await File.WriteAllBytesAsync(file, [1]);

        foreach (BrowseStartMode mode in (BrowseStartMode[])[BrowseStartMode.FixedFolder, BrowseStartMode.SystemDefault])
        {
            AppSettings settings = new() { BrowseStartMode = mode };
            Mock<IDialogService> dialogs = new();
            dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync([file]);

            WizardSourcesViewModel vm = Build(settings, dialogs.Object);
            await vm.AddFilesCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, settings.LastBrowsedFolder);
        }
    }

    [Fact]
    public async Task TheRememberedFolderSurvivesARestart()
    {
        string folder = Path.Combine(_root, "persisted");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.bin");
        await File.WriteAllBytesAsync(file, [1]);

        SettingRepository repo = new(_factory);
        AppSettings settings = new() { BrowseStartMode = BrowseStartMode.LastUsed };
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync([file]);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object, repo);
        await vm.AddFilesCommand.ExecuteAsync(null);

        // In-memory is only half of "remember" — the row has to reach the DB for the next launch.
        SettingDto? saved = await WaitForSettingAsync(repo, SettingKey.LastBrowsedFolder);
        Assert.NotNull(saved);
        Assert.Equal(folder, saved!.Value);
    }

    // ── The plumbing that was missing entirely ──

    [Fact]
    public async Task TheFilePickerIsActuallyTOLDWhereToOpen()
    {
        // BrowseFilesAsync had no start-directory parameter at all, so every resolution above would
        // have been computed and then thrown away. This pins that it reaches the dialog.
        AppSettings settings = new()
        {
            BrowseStartMode = BrowseStartMode.FixedFolder,
            BrowseStartFolder = _root,
        };
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string[]?)null);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object);
        await vm.AddFilesCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), _root), Times.Once);
    }

    [Fact]
    public async Task TheFolderPickerIsGivenTheSameAnswer()
    {
        AppSettings settings = new()
        {
            BrowseStartMode = BrowseStartMode.FixedFolder,
            BrowseStartFolder = _root,
        };
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string[]?)null);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object);
        await vm.AddFoldersCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.BrowseFoldersAsync(_root, It.IsAny<string?>()), Times.Once);
    }

    private WizardSourcesViewModel Build(
        AppSettings? settings,
        IDialogService? dialogService = null,
        SettingRepository? repo = null)
        => new(
            dialogService ?? Mock.Of<IDialogService>(),
            Mock.Of<IAppLogger>(),
            markSummaryDirty: () => { },
            revalidateHosters: () => { },
            settings,
            repo);

    /// <summary>The persist is fire-and-forget (a pick must not wait on a DB write), so poll briefly
    /// rather than assume it has landed by the time the command returns.</summary>
    private static async Task<SettingDto?> WaitForSettingAsync(SettingRepository repo, string key)
    {
        for (int i = 0; i < 50; i++)
        {
            SettingDto? found = await repo.FindByKeyAsync(key);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(20);
        }

        return null;
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
