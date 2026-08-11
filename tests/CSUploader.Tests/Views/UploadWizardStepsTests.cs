// <copyright file="UploadWizardStepsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="UploadWizardWindow"/> steps 1 (hosters grid) and 3 (start
/// mode + scheduled DatePicker) — Phase 6 Task 8. The load-bearing checks:
/// <list type="bullet">
///   <item><description><b>EnumBool two-way (prep 8 / Reality-check #21)</b>: an external <c>StartMode</c>
///   change re-checks the matching RadioButton; clicking a button writes the enum; and unchecking a button
///   returns <c>BindingOperations.DoNothing</c> so the enum is never nulled — its FIRST real exercise on a
///   dedicated (non-mode) group;</description></item>
///   <item><description><b>DatePicker converter (rule 36 / prep 3)</b>: the scheduled DatePicker round-trips
///   the non-nullable <see cref="System.DateTime"/> <c>ScheduledDate</c> through <c>DateTimeOffsetConverter</c>
///   in the LIVE binding, including the null-clear DoNothing path;</description></item>
///   <item><description><b>hosters grid</b>: the <c>Use</c> checkbox two-way-writes, <c>!CanUse</c> hides the
///   checkbox and shows the account-required glyph, the "Add account…" link runs the command, and the
///   validation-warnings border follows <c>HasHosterValidationWarnings</c>.</description></item>
/// </list>
/// The wizard is NEVER driven through GoNext at step 3 (that path calls StartUploadAsync — a real upload);
/// every shown window is closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class UploadWizardStepsTests
{
    // ── Step 3: the start-mode EnumBool group two-way, both directions + the DoNothing uncheck (prep 8) ──

    [AvaloniaFact]
    public void StartModeRadios_EnumBoolTwoWay_BothDirections_AndDoNothingUncheckKeepsTheEnum()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Default StartMode=Immediately → only the Immediately radio is checked.
            Assert.Equal(UploadStartMode.Immediately, harness.Vm.StartMode);
            Assert.True(wizard.ImmediatelyRadio.IsChecked);
            Assert.False(wizard.LaterRadio.IsChecked);
            Assert.False(wizard.ScheduledRadio.IsChecked);

            // External enum change re-checks the matching button and unchecks the others (Convert path).
            harness.Vm.StartMode = UploadStartMode.Scheduled;
            Dispatcher.UIThread.RunJobs();
            Assert.True(wizard.ScheduledRadio.IsChecked);
            Assert.False(wizard.ImmediatelyRadio.IsChecked);
            Assert.False(wizard.LaterRadio.IsChecked);

            // Clicking a button writes the enum (ConvertBack true path). The group unchecks the previously
            // checked Scheduled radio, whose ConvertBack(false) returns DoNothing — so StartMode stays Later,
            // it is NOT nulled by the uncheck.
            wizard.LaterRadio.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UploadStartMode.Later, harness.Vm.StartMode);
            Assert.False(wizard.ScheduledRadio.IsChecked);
            Assert.False(wizard.ImmediatelyRadio.IsChecked);

            // Isolate the DoNothing path: directly uncheck the only-checked button. ConvertBack(false) →
            // DoNothing aborts the write, so the enum keeps its value (never nulled/reset — Reality-check #21).
            wizard.LaterRadio.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UploadStartMode.Later, harness.Vm.StartMode);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 3: the scheduled row shows/hides with IsScheduledMode ──

    [AvaloniaFact]
    public void ScheduledRow_VisibilityFollowsIsScheduledMode()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Immediately (default) → the scheduled DatePicker row is hidden.
            Assert.False(harness.Vm.IsScheduledMode);
            Assert.False(wizard.ScheduledRow.IsVisible);

            harness.Vm.StartMode = UploadStartMode.Scheduled;
            Dispatcher.UIThread.RunJobs();
            Assert.True(wizard.ScheduledRow.IsVisible);

            harness.Vm.StartMode = UploadStartMode.Later;
            Dispatcher.UIThread.RunJobs();
            Assert.False(wizard.ScheduledRow.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 3: the DatePicker round-trips ScheduledDate through DateTimeOffsetConverter (rule 36) ──

    [AvaloniaFact]
    public void ScheduledDatePicker_RoundTripsScheduledDate_ThroughTheConverter_AndNullClearIsANoOp()
    {
        using VmHarness harness = new();
        harness.Vm.StartMode = UploadStartMode.Scheduled;
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Convert: the VM's non-nullable DateTime seeds the DateTimeOffset? picker (same calendar day).
            Assert.NotNull(wizard.ScheduledDatePicker.SelectedDate);
            Assert.Equal(harness.Vm.ScheduledDate.Date, wizard.ScheduledDatePicker.SelectedDate!.Value.Date);

            // ConvertBack: picking a date writes it back through the converter to the non-nullable DateTime.
            var picked = new DateTimeOffset(new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Unspecified));
            wizard.ScheduledDatePicker.SelectedDate = picked;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DateTime(2027, 3, 15), harness.Vm.ScheduledDate.Date);

            // Null clear: ConvertBack(null) → DoNothing, so a cleared picker leaves ScheduledDate untouched
            // (it must not zero the non-nullable default) — the DoNothing sentinel proven in the live binding.
            wizard.ScheduledDatePicker.SelectedDate = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DateTime(2027, 3, 15), harness.Vm.ScheduledDate.Date);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the Use checkbox two-way-writes; !CanUse gates the checkbox vs the account-required glyph ──

    [AvaloniaFact]
    public void HostersGrid_UseCheckboxTwoWay_AndCanUseGatesCheckboxVsGlyph()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel usable = new("Catbox", [new FileHosterLoginDto { FileHosterName = "Catbox", Username = "me" }]);
        FileHosterSelectionViewModel blocked = new("Nowhere", []);
        harness.Vm.FileHosters.Add(usable);
        harness.Vm.FileHosters.Add(blocked);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // The grid reads a filtered VIEW over the collection, not the collection itself — that is
            // what lets a ticked hoster stay in the upload after the filter hides it. The view's source
            // is still the VM's list.
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.fileHostersGrid.ItemsSource);
            Assert.Same(harness.Vm.FileHosters, view.SourceCollection);

            DataGridRow usableRow = RowFor(wizard.fileHostersGrid, usable);
            DataGridRow blockedRow = RowFor(wizard.fileHostersGrid, blocked);

            // Usable row: the enable checkbox shows, the account-required glyph is hidden.
            CheckBox usableCheck = CheckboxIn(usableRow);
            Assert.True(usableCheck.IsVisible);
            Assert.False(GlyphIn(usableRow).IsVisible);

            // Toggling the checkbox two-way-writes Use onto the row VM.
            usableCheck.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(usable.Use);

            // Blocked row (no accounts, no anonymous): the checkbox is hidden, the glyph shows.
            Assert.False(CheckboxIn(blockedRow).IsVisible);
            Assert.True(GlyphIn(blockedRow).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the filter bar narrows the GRID without touching the list the upload is built from ──

    [AvaloniaFact]
    public void HostersGrid_FilterBar_NarrowsTheGrid_AndKeepsTickedHostersInTheUpload()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel catbox = new("Catbox", [], supportsAnonymous: true);
        FileHosterSelectionViewModel rapidgator = new("Rapidgator", [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }]);
        harness.Vm.FileHosters.Add(catbox);
        harness.Vm.FileHosters.Add(rapidgator);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.fileHostersGrid.ItemsSource);
            Assert.Equal(2, view.Count);

            // Tick a hoster, THEN filter it away: the grid stops showing it and the row keeps its tick,
            // because the filter is a view over the collection rather than a rewrite of it.
            catbox.Use = true;
            harness.Vm.HosterFilterText = "rapid";
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view);
            Assert.DoesNotContain(catbox, view.Cast<object>());
            Assert.Contains(catbox, harness.Vm.FileHosters);
            Assert.True(catbox.Use);

            // Anonymous-only swaps which one is left — Rapidgator needs an account.
            harness.Vm.HosterFilterText = string.Empty;
            harness.Vm.AnonymousHostersOnly = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view);
            Assert.Contains(catbox, view.Cast<object>());

            // Clearing restores the whole list.
            harness.Vm.ClearHosterFilterCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, view.Count);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the two cap columns render the row's text, including "No limit" for the common case ──

    [AvaloniaFact]
    public void HostersGrid_ShowsMaxFileSizeAndMaxParallelColumns()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel capped = new(
            "DropMeFiles",
            [],
            supportsAnonymous: true,
            maxFileSizeResolver: _ => 53_687_091_200,
            maxConcurrentResolver: _ => 5);
        FileHosterSelectionViewModel uncapped = new(
            "Catbox",
            [new FileHosterLoginDto { FileHosterName = "Catbox", Username = "me" }],
            maxFileSizeResolver: _ => null,
            maxConcurrentResolver: _ => null);
        harness.Vm.FileHosters.Add(capped);
        harness.Vm.FileHosters.Add(uncapped);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            string noLimit = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_NoLimit"];

            List<string?> cappedTexts = RowFor(wizard.fileHostersGrid, capped)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            List<string?> uncappedTexts = RowFor(wizard.fileHostersGrid, uncapped)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains("5", cappedTexts);                 // the hoster's own parallel ceiling
            Assert.Contains("50 GiB", cappedTexts);            // …alongside its size cap
            Assert.Equal(2, uncappedTexts.Count(t => t == noLimit)); // both columns read "No limit"
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the "Add account…" link runs AddAccountForHosterCommand for its row ──

    [AvaloniaFact]
    public void AddAccountLink_RunsAddAccountForHosterCommand_ForItsRow()
    {
        Mock<IDialogService> dialog = new();
        dialog
            .Setup(d => d.ShowAddAccountDialogAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<Func<string, Task<AccountCheckResult>>>(),
                It.IsAny<string?>()))
            .ReturnsAsync((FileHosterLoginDto?)null);

        using VmHarness harness = new(dialog.Object);
        FileHosterSelectionViewModel blocked = new("Nowhere", []);
        harness.Vm.FileHosters.Add(blocked);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // The code-behind seam the link's left-button PointerReleased delegates to (the sanctioned
            // fallback for a cell-template link — a real pointer release can't be synthesized headless).
            wizard.InvokeAddAccountForHoster(blocked);
            Dispatcher.UIThread.RunJobs();

            dialog.Verify(
                d => d.ShowAddAccountDialogAsync(
                    "Nowhere",
                    It.IsAny<string[]>(),
                    It.IsAny<Func<string, Task<AccountCheckResult>>>(),
                    It.IsAny<string?>()),
                Times.Once);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the validation-warnings border follows HasHosterValidationWarnings (rule 33) ──

    [AvaloniaFact]
    public void HosterWarningsBorder_VisibilityFollowsHasHosterValidationWarnings()
    {
        // No warnings → the border is hidden.
        using VmHarness empty = new();
        empty.Vm.CurrentStep = 1;
        (Window emptyWindow, UploadWizardWindow emptyWizard) = Show(empty.Vm);

        // A pre-seeded warning → the border is visible (HasHosterValidationWarnings reads Count>0 at bind).
        using VmHarness warned = new();
        warned.Vm.HosterValidationWarnings.Add("Catbox: 3 files exceed the size limit");
        warned.Vm.CurrentStep = 1;
        (Window warnedWindow, UploadWizardWindow warnedWizard) = Show(warned.Vm);
        try
        {
            Assert.False(emptyWizard.HosterWarningsBorder.IsVisible);
            Assert.True(warnedWizard.HosterWarningsBorder.IsVisible);
        }
        finally
        {
            emptyWindow.Close();
            warnedWindow.Close();
        }
    }

    // ── helpers ──

    private static DataGridRow RowFor(DataGrid grid, FileHosterSelectionViewModel row)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, row));

    private static CheckBox CheckboxIn(DataGridRow row)
        => row.GetVisualDescendants().OfType<CheckBox>().First();

    // The account-required indicator in the Use column: a named vector padlock Path (was a Segoe MDL2
    // glyph). Named so it is not confused with the CheckBox template's own checkmark Path in the row.
    private static Control GlyphIn(DataGridRow row)
        => row.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>().First(p => p.Name == "UseLockGlyph");

    private static (Window Window, UploadWizardWindow Wizard) Show(UploadWizardViewModel vm)
    {
        UploadWizardWindow wizard = new(vm);
        wizard.Show();
        Dispatcher.UIThread.RunJobs();
        return (wizard, wizard);
    }

    /// <summary>
    /// A real <see cref="UploadWizardViewModel"/> over an in-memory SQLite DB — the same scratch-repo harness
    /// the shell tests use (the WPF <c>UploadWizardViewModelTests</c> shape). Steps 1/3 are exercised by
    /// direct property/collection sets, never through GoNext, so the PackageManager only satisfies the ctor.
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
