// <copyright file="UploadWizardShellTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
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

    // ── Mode RadioButtons two-way Mode + the source pickers follow ──

    [AvaloniaFact]
    public void ModeRadios_TwoWayBindMode_AndSourcePickersFollow_BothDirections()
    {
        using VmHarness harness = new();
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Default is Directory: the directory picker shows, the files picker is hidden.
            Assert.Equal(UploadWizardMode.Directory, harness.Vm.Mode);
            Assert.True(wizard.DirectoryModeRadio.IsChecked);
            Assert.True(wizard.DirectorySourcePicker.IsVisible);
            Assert.False(wizard.FilesSourcePicker.IsVisible);

            // Click the Files radio → the two-way EnumBool binding writes Files into the VM and the pickers swap.
            wizard.FilesModeRadio.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UploadWizardMode.Files, harness.Vm.Mode);
            Assert.False(wizard.DirectorySourcePicker.IsVisible);
            Assert.True(wizard.FilesSourcePicker.IsVisible);

            // An external VM change re-checks the matching radio and swaps the pickers back.
            harness.Vm.Mode = UploadWizardMode.Directory;
            Dispatcher.UIThread.RunJobs();
            Assert.True(wizard.DirectoryModeRadio.IsChecked);
            Assert.False(wizard.FilesModeRadio.IsChecked);
            Assert.True(wizard.DirectorySourcePicker.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── The files grid realizes rows, and an IsVisible=false row collapses (§Reality-check #20) ──

    [AvaloniaFact]
    public void FilesGrid_RealizesRows_AndHidesAnInvisibleRow()
    {
        using VmHarness harness = new();
        FileEntry visibleA = MakeEntry("a.mkv", 10);
        FileEntry hidden = MakeEntry("b.txt", 20);
        FileEntry visibleC = MakeEntry("c.zip", 30);
        hidden.IsVisible = false;
        harness.Vm.Files.Add(visibleA);
        harness.Vm.Files.Add(hidden);
        harness.Vm.Files.Add(visibleC);

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.Same(harness.Vm.Files, wizard.filesGrid.ItemsSource);

            // All three rows realize; the filtered (IsVisible=false) one is collapsed, the others shown.
            Assert.False(RowFor(wizard.filesGrid, hidden).IsVisible);
            Assert.True(RowFor(wizard.filesGrid, visibleA).IsVisible);
            Assert.True(RowFor(wizard.filesGrid, visibleC).IsVisible);

            // Flipping IsVisible back on shows the row (the scoped DataGridRow style tracks the item).
            hidden.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(RowFor(wizard.filesGrid, hidden).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Cancel closes with a non-completed result (rule 7) ──

    [AvaloniaFact]
    public async Task Cancel_ClosesWithNonCompletedResult()
    {
        using VmHarness harness = new();
        var owner = new Window { Width = 200, Height = 200 };
        var wizard = new UploadWizardWindow(harness.Vm);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<bool?> dialog = wizard.ShowDialog<bool?>(owner);
            Dispatcher.UIThread.RunJobs();

            Click(wizard.CancelButton);
            Dispatcher.UIThread.RunJobs();

            bool? result = await dialog;
            Assert.NotEqual(true, result); // cancelled, not the Completed→Close(true) result
            Assert.False(harness.Vm.Completed);
        }
        finally
        {
            wizard.Close();
            owner.Close();
        }
    }

    // ── The Next button's content + enabled state track NextButtonText / CanGoNext ──

    [AvaloniaFact]
    public void NextButton_TracksNextButtonTextAndCanGoNext()
    {
        using VmHarness harness = new();
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Step 0: Next is enabled (CanGoNext) and reads the "Next" label.
            Assert.True(harness.Vm.CanGoNext);
            Assert.True(wizard.NextButton.IsEnabled);
            Assert.Equal(harness.Vm.NextButtonText, wizard.NextButton.Content);

            // Jump the step to the last (a plain property set — NOT GoNext, which would upload): the button
            // label follows to the "Add" text. Back also becomes visible (CanGoBack).
            harness.Vm.CurrentStep = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.True(harness.Vm.IsLastStep);
            Assert.Equal(harness.Vm.NextButtonText, wizard.NextButton.Content);
            Assert.True(wizard.BackButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ──

    private static FileEntry MakeEntry(string name, long size) => new()
    {
        FullPath = name,
        RelativePath = name,
        FileName = name,
        Size = size,
        IsSelected = true,
        IsVisible = true,
    };

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
