// <copyright file="UploadWizardShellTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static CSUploader.Tests.Avalonia.HeadlessInput;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="UploadWizardWindow"/> shell + step 0 (Phase 6 Task 7).
/// The load-bearing checks: the wizard VM's seven ctor dependencies all resolve against the head's DI graph
/// (the VM is DI-registered Transient and resolved from <c>App.Services</c>, Reality-check #9); step 0 is visible at <c>CurrentStep=0</c>;
/// the mode RadioButtons two-way <see cref="UploadWizardViewModel.Mode"/> (Directory↔Files) and the source
/// pickers follow; added <see cref="FileEntry"/> rows realize and an <c>IsVisible=false</c> row collapses
/// (§Reality-check #20 — the scoped DataGridRow style); Cancel closes with a non-completed result (rule 7);
/// and the Next button's content + enabled state track <c>NextButtonText</c> / <c>CanGoNext</c>. The wizard is
/// NEVER driven through GoNext at step 3 (that path calls StartUploadAsync — a real upload). Every shown
/// window is closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class UploadWizardShellTests
{
    // --- The seven wizard-ctor dependencies resolve from the head DI graph (Reality-check #9) ---

    [AvaloniaFact]
    public void WizardViewModel_SevenCtorDependencies_AllResolveFromTheHeadDiGraph()
    {
        // UploadWizardViewModel is DI-registered (Transient) and the window resolves it from App.Services; its
        // ctor depends on exactly these seven services, so this asserts each is present in the graph
        // the head composes at startup — a missing registration would throw at wizard-open time in production.
        string tempDir = Path.Combine(Path.GetTempPath(), "csu-ava-wizard-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ServiceCollection services = new();
            App.ConfigureServices(services, tempDir);
            using ServiceProvider provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<PackageManager>());
            Assert.NotNull(provider.GetRequiredService<FileHosterLoginRepository>());
            Assert.NotNull(provider.GetRequiredService<IDialogService>());
            Assert.NotNull(provider.GetRequiredService<IAppLogger>());
            Assert.NotNull(provider.GetRequiredService<AppSettings>());
            Assert.NotNull(provider.GetRequiredService<IFileHosterRegistry>());
            Assert.NotNull(provider.GetRequiredService<IAccountVerifier>());
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ── Step 0 is the live step at CurrentStep=0 ──

    [AvaloniaFact]
    public void CurrentStepZero_ShowsStepZeroPanel()
    {
        using VmHarness harness = new();
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.Equal(0, harness.Vm.CurrentStep);
            Assert.True(wizard.Step0Panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── The source tree: structure, scoping, and removing a source from it ──

    [AvaloniaFact]
    public void SourceTree_ShowsWhatWasAdded_ScopesTheGrid_AndRemovesASource()
    {
        // This replaces first a mode-radio test, then a Sources-strip one: the strip sat ABOVE the grid
        // and cost it height per folder added, so it became a column.
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "subs"));
        File.WriteAllText(Path.Combine(dir, "a.mkv"), "a");
        File.WriteAllText(Path.Combine(dir, "subs", "a.srt"), "s");
        string loose = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".nfo");
        File.WriteAllText(loose, "n");

        using VmHarness harness = new();
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            harness.Vm.Sources.AddDroppedPaths([dir, loose]);
            Dispatcher.UIThread.RunJobs();

            // One All root, holding the folder (with its subfolder) and the loose-files bucket.
            UploadTreeNode all = Assert.Single(harness.Vm.Sources.TreeRoots);
            Assert.Equal(UploadTreeNodeKind.All, all.Kind);
            Assert.Equal(3, all.FileCount);
            Assert.Equal(2, all.Children.Count);

            UploadTreeNode folder = all.Children.First(c => c.Kind == UploadTreeNodeKind.Folder);
            Assert.Equal(2, folder.FileCount);                     // its own file plus the one in subs
            Assert.Single(folder.Children);                        // subs
            Assert.Equal("subs", folder.Children[0].Name);

            // Selecting the folder scopes the GRID's view to it AND everything beneath.
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.filesGrid.ItemsSource);
            harness.Vm.Sources.SelectedNode = folder;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, view.Count);
            Assert.DoesNotContain(view.Cast<FileEntry>(), f => f.FullPath == loose);

            // Back to All: everything shows again, each in its own row.
            harness.Vm.Sources.SelectedNode = all;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, view.Count);
            DataGridRow[] rows = ShownRows(wizard.filesGrid);
            Assert.Equal(3, rows.Length);
            Assert.Equal(rows.Length, rows.Select(r => r.Bounds.Y).Distinct().Count());

            // Unticking one leaf leaves the branch partial, which is what the tri-state box shows.
            harness.Vm.Sources.Files.First(f => f.FileName == "a.srt").IsSelected = false;
            Assert.Null(folder.IsChecked);
            Assert.Null(all.IsChecked);

            // Removing the source takes its whole branch with it, leaving the loose file.
            harness.Vm.Sources.RemoveSourceCommand.Execute(folder.Source);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(harness.Vm.Sources.Files);
            Assert.Single(Assert.Single(harness.Vm.Sources.TreeRoots).Children);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
            File.Delete(loose);
        }
    }

    // ── The files grid realizes rows, and a filtered-out file is ABSENT from the view ──

    [AvaloniaFact]
    public void FilesGrid_RealizesRows_AndAFilteredFileLeavesTheView()
    {
        // This used to assert the opposite mechanism: the row stayed and was COLLAPSED. That leaves a
        // zero-height row inside the presenter's layout, and one re-shown after being collapsed could
        // be drawn over its neighbour — which is what two files reappearing from a de-selected folder
        // looked like on screen. The view simply doesn't contain a filtered file now.
        using VmHarness harness = new();
        FileEntry a = MakeEntry("a.mkv", 10);
        FileEntry b = MakeEntry("b.txt", 20);
        FileEntry c = MakeEntry("c.zip", 30);
        harness.Vm.Sources.Files.Add(a);
        harness.Vm.Sources.Files.Add(b);
        harness.Vm.Sources.Files.Add(c);

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.filesGrid.ItemsSource);
            Assert.Same(harness.Vm.Sources.Files, view.SourceCollection);
            Assert.Equal(3, view.Count);
            Assert.Equal(3, wizard.filesGrid.GetVisualDescendants().OfType<DataGridRow>().Count());

            // Filtering to one name drops the other two OUT of the view — no collapsed rows left behind.
            harness.Vm.Sources.FileFilter = "b.txt";
            Dispatcher.UIThread.RunJobs();
            Assert.Single(view);
            Assert.DoesNotContain(a, view.Cast<object>());

            // The grid keeps recycled spares around and hides them ITSELF; what matters is that no
            // filtered item is still bound to a shown row.
            Assert.DoesNotContain(
                ShownRows(wizard.filesGrid),
                r => ReferenceEquals(r.DataContext, a) || ReferenceEquals(r.DataContext, c));

            // Clearing it brings them back, each in its own row.
            harness.Vm.Sources.FileFilter = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, view.Count);

            // No two SHOWN rows may occupy the same vertical slot — the artifact this change removes.
            DataGridRow[] rows = ShownRows(wizard.filesGrid);
            Assert.Equal(3, rows.Length);
            Assert.Equal(rows.Length, rows.Select(r => r.Bounds.Y).Distinct().Count());
        }
        finally
        {
            window.Close();
        }
    }

    private static FileEntry MakeEntry(string name, long size) => new()
    {
        FullPath = name,
        RelativePath = name,
        FileName = name,
        Size = size,
        IsSelected = true,
    };

    /// <summary>The rows the user can actually see. A DataGrid keeps recycled spares in its visual
    /// tree and hides them itself, so a raw descendant walk over-counts.</summary>
    private static DataGridRow[] ShownRows(DataGrid grid)
        => [.. grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible)];

    private static DataGridRow RowFor(DataGrid grid, FileEntry entry)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, entry));

    private static (Window Window, UploadWizardWindow Wizard) Show(UploadWizardViewModel vm)
    {
        // The wizard IS a Window, so it is shown directly (the EditAccountWindow test pattern). Its own
        // 850×620 size realizes the step-0 grid rows in the headless surface.
        UploadWizardWindow wizard = new(vm);
        wizard.Show();
        Dispatcher.UIThread.RunJobs();
        return (wizard, wizard);
    }

    /// <summary>
    /// A real <see cref="UploadWizardViewModel"/> over an in-memory SQLite DB — the scratch-repo harness the
    /// WPF <c>UploadWizardViewModelTests</c> uses (no <see cref="IFileHosterRegistry"/> / <see cref="IAccountVerifier"/>,
    /// which are optional). Step-0 tests never reach StartUploadAsync, so the PackageManager is only wired to
    /// satisfy the non-null ctor dependency.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UploadScheduler _scheduler;

        public VmHarness(IDialogService? dialog = null)
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

            FileHosterLoginRepository loginRepo = new(factory);
            AppSettings settings = new();
            DefaultFileHosterRegistry registry = new([]);
            _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
            PackageManager packageManager = new(
                settings,
                _scheduler,
                new UploadPackageRepository(factory),
                new UploadPackageFileRepository(factory),
                loginRepo,
                Mock.Of<IAppLogger>(),
                registry);

            Vm = new UploadWizardViewModel(packageManager, loginRepo, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), settings);
        }

        public UploadWizardViewModel Vm { get; }

        public void Dispose()
        {
            _scheduler.Dispose();
            _connection.Dispose();
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

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
