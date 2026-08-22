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
/// <para>
/// There is no mode control: <see cref="AppSettings.DefaultUploadDirectory"/> being EMPTY is the
/// mode, and means "reopen where I last was". So the resolution is a two-step fallback chain, and
/// most of what is worth pinning here is the order of it.
/// </para>
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

    // ── The fallback chain: configured directory, then last used, then nothing ──

    [Fact]
    public void AConfiguredDirectory_Wins_EvenOverWhereTheUserLastWas()
    {
        // The staging-directory case: someone who always uploads from one place set it deliberately,
        // so browsing elsewhere once must not quietly move it.
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            DefaultUploadDirectory = _root,
            LastBrowsedFolder = Path.Combine(_root, "somewhere-else"),
        });

        Assert.Equal(_root, vm.ResolveBrowseStart());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyDirectory_FallsBackToWhereTheUserLastWas(string configured)
    {
        // Empty is not "no preference expressed and therefore nothing to do" — it IS the preference,
        // and it means last-used. Whitespace counts as empty; a path box people clear by hand ends
        // up with a stray space more often than not.
        string lastUsed = Path.Combine(_root, "last");
        WizardSourcesViewModel vm = Build(new AppSettings
        {
            DefaultUploadDirectory = configured,
            LastBrowsedFolder = lastUsed,
        });

        Assert.Equal(lastUsed, vm.ResolveBrowseStart());
    }

    [Fact]
    public void WithNeitherSet_ItIsNoWorseThanBeforeAnyOfThisExisted()
    {
        // The pre-setting fallback: the last folder added in this wizard. A first-ever run has none
        // of the three, and then there is genuinely nothing to suggest.
        WizardSourcesViewModel vm = Build(new AppSettings());
        Assert.Null(vm.ResolveBrowseStart());

        string folder = Path.Combine(_root, "season-1");
        Directory.CreateDirectory(folder);
        vm.AddDroppedPaths([folder]);

        Assert.Equal(folder, vm.ResolveBrowseStart());
    }

    [Fact]
    public void WithNoSettingsAtAll_NothingThrows()
    {
        // Null settings is the shape older callers and tests construct.
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

        AppSettings settings = new();
        WizardSourcesViewModel vm = Build(settings, FilePicker(file));
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

        AppSettings settings = new();
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync([picked]);

        WizardSourcesViewModel vm = Build(settings, dialogs.Object);
        await vm.AddFoldersCommand.ExecuteAsync(null);

        Assert.Equal(parent, settings.LastBrowsedFolder);
    }

    [Fact]
    public async Task ItKeepsRemembering_EvenWhileAConfiguredDirectoryIsWinning()
    {
        // The configured directory suppresses the FALLBACK, not the bookkeeping. Clearing that box
        // should land somewhere useful immediately rather than on a cold start.
        string folder = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.bin");
        await File.WriteAllBytesAsync(file, [1]);

        AppSettings settings = new() { DefaultUploadDirectory = _root };
        WizardSourcesViewModel vm = Build(settings, FilePicker(file));
        await vm.AddFilesCommand.ExecuteAsync(null);

        Assert.Equal(folder, settings.LastBrowsedFolder);
        Assert.Equal(_root, vm.ResolveBrowseStart());   // still winning…

        settings.DefaultUploadDirectory = string.Empty;
        Assert.Equal(folder, vm.ResolveBrowseStart());  // …and clearing it lands on the remembered one
    }

    [Fact]
    public async Task TheRememberedFolderSurvivesARestart()
    {
        string folder = Path.Combine(_root, "persisted");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.bin");
        await File.WriteAllBytesAsync(file, [1]);

        SettingRepository repo = new(_factory);
        AppSettings settings = new();
        WizardSourcesViewModel vm = Build(settings, FilePicker(file), repo);
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
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string[]?)null);

        WizardSourcesViewModel vm = Build(new AppSettings { DefaultUploadDirectory = _root }, dialogs.Object);
        await vm.AddFilesCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), _root), Times.Once);
    }

    [Fact]
    public async Task TheFolderPickerIsGivenTheSameAnswer()
    {
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFoldersAsync(It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((string[]?)null);

        WizardSourcesViewModel vm = Build(new AppSettings { DefaultUploadDirectory = _root }, dialogs.Object);
        await vm.AddFoldersCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.BrowseFoldersAsync(_root, It.IsAny<string?>()), Times.Once);
    }

    private static IDialogService FilePicker(string returns)
    {
        Mock<IDialogService> dialogs = new();
        dialogs.Setup(d => d.BrowseFilesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync([returns]);
        return dialogs.Object;
    }

    private static WizardSourcesViewModel Build(
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
