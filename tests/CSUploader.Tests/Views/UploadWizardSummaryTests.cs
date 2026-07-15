// <copyright file="UploadWizardSummaryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
/// Headless verification of the ported <see cref="UploadWizardWindow"/> step 2 (Summary) — Phase 6 Task 9.
/// The load-bearing checks:
/// <list type="bullet">
///   <item><description><b>per-hoster custom Expander (rule 38)</b>: <c>Summaries</c> renders one
///   <c>SummaryExpanderTheme</c> Expander per hoster; each starts expanded and its chevron ToggleButton
///   collapses the body (the theme's <c>:checked</c>/<c>:expanded</c> wiring, TwoWay to <c>IsExpanded</c>);</description></item>
///   <item><description><b>per-hoster summary binding</b>: the header shows <c>HosterName</c>/<c>IncludedSummary</c>;
///   <c>IsOverCapacity</c> recolors the capacity line (the <c>capacity-over</c> class, rule 40) and reveals the
///   capacity-error line; the auto-fit / orphan banners follow their flags (rule 33);</description></item>
///   <item><description><b>per-file include checkbox</b>: a file-row checkbox two-way-writes
///   <c>SummaryFileItem.Included</c> and nudges the hoster's capacity recompute (over → under).</description></item>
/// </list>
/// Summaries are constructed directly (the public <see cref="HosterUploadSummary"/>/<see cref="SummaryFileItem"/>
/// ctors) — the wizard is NEVER stepped through GoNext (that path would trigger a real storage refresh / upload).
/// Every shown window is closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class UploadWizardSummaryTests
{
    // ── One SummaryExpanderTheme Expander per hoster; the header carries HosterName + IncludedSummary ──

    [AvaloniaFact]
    public void Summaries_RenderOneExpanderPerHoster_WithHeaderText()
    {
        using VmHarness harness = new();
        // Enter step 2 FIRST: the VM starts _summaryDirty, so the first CurrentStep==2 rebuilds Summaries from
        // the (empty) selection and clears the dirty flag; seeding afterward survives (the ItemsControl picks up
        // the CollectionChanged). Seeding before would be wiped by that rebuild.
        harness.Vm.CurrentStep = 2;
        harness.Vm.Summaries.Add(MakeSummary("Catbox", "me@example.test", available: null, ("clip.mkv", 4096, true)));
        harness.Vm.Summaries.Add(MakeSummary("Gofile", "anon", available: null, ("archive.zip", 2048, true)));

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.Same(harness.Vm.Summaries, wizard.SummariesList.ItemsSource);

            List<Expander> expanders = Expanders(wizard);
            Assert.Equal(2, expanders.Count);

            // Each hoster's header renders its name and the checked-files summary line.
            Expander catbox = ExpanderFor(wizard, harness.Vm.Summaries[0]);
            List<string?> texts = catbox.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Catbox", texts);
            Assert.Contains(texts, t => t == harness.Vm.Summaries[0].IncludedSummary);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Each Expander starts expanded; its chevron ToggleButton collapses the body (rule 38 theme wiring) ──

    [AvaloniaFact]
    public void Expander_StartsExpanded_AndChevronToggleCollapsesTheBody()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 2; // clear _summaryDirty before seeding (see the render test's note)
        harness.Vm.Summaries.Add(MakeSummary("Catbox", "me", available: null, ("clip.mkv", 4096, true)));

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Expander expander = Expanders(wizard).Single();
            ContentPresenter body = BodyPresenter(expander);

            // Starts expanded (IsExpanded=True in the template) → the content presenter is visible.
            Assert.True(expander.IsExpanded);
            Assert.True(body.IsVisible);

            // The chevron ToggleButton is IsChecked TwoWay to IsExpanded: unchecking it collapses the Expander
            // and (via the :expanded /template/ style) hides the body.
            ToggleButton chevron = expander.GetVisualDescendants().OfType<ToggleButton>().First();
            Assert.True(chevron.IsChecked);
            chevron.IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(expander.IsExpanded);
            Assert.False(body.IsVisible);

            // Re-checking expands it again (the round trip).
            chevron.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(expander.IsExpanded);
            Assert.True(body.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── IsOverCapacity recolors the capacity line (capacity-over class) and reveals the capacity-error line ──

    [AvaloniaFact]
    public void OverCapacity_AddsCapacityOverClass_AndShowsCapacityError()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 2; // clear _summaryDirty before seeding (see the render test's note)
        // 2 KiB checked against a 1 KiB free quota → over capacity from the start.
        HosterUploadSummary summary = MakeSummary("Rapidgator", "user", available: 1024, ("big.bin", 2048, true));
        harness.Vm.Summaries.Add(summary);

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.True(summary.IsOverCapacity);
            Expander expander = Expanders(wizard).Single();

            // The capacity line (bound to CapacityDisplay, shown for HasQuota) carries the capacity-over class.
            TextBlock capacity = expander.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == summary.CapacityDisplay);
            Assert.Contains("capacity-over", capacity.Classes);

            // The over-capacity hint line (bound to CapacityError) is visible.
            TextBlock error = expander.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == summary.CapacityError);
            Assert.True(error.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── A per-file include checkbox two-way-writes Included and recomputes the hoster's capacity ──

    [AvaloniaFact]
    public void PerFileIncludeCheckbox_TwoWayWrites_AndRecomputesCapacity()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 2; // clear _summaryDirty before seeding (see the render test's note)
        // Two 800-byte files against a 1 KiB quota → both included = 1600 > 1024 (over); unchecking one → 800 (under).
        HosterUploadSummary summary = MakeSummary(
            "Rapidgator", "user", available: 1024, ("a.bin", 800, true), ("b.bin", 800, true));
        harness.Vm.Summaries.Add(summary);

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.True(summary.IsOverCapacity);
            Assert.Equal(2, summary.IncludedCount);

            Expander expander = Expanders(wizard).Single();
            // The per-file rows live in the Expander body; skip the chevron toggle by matching only real
            // CheckBoxes bound to a SummaryFileItem.
            List<CheckBox> fileChecks = expander.GetVisualDescendants().OfType<CheckBox>()
                .Where(c => c.DataContext is SummaryFileItem).ToList();
            Assert.Equal(2, fileChecks.Count);
            Assert.All(fileChecks, c => Assert.True(c.IsChecked));

            // Unchecking one file two-way-writes Included=false and nudges the recompute below capacity.
            var firstItem = (SummaryFileItem)fileChecks[0].DataContext!;
            fileChecks[0].IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(firstItem.Included);
            Assert.Equal(1, summary.IncludedCount);
            Assert.False(summary.IsOverCapacity);
        }
        finally
        {
            window.Close();
        }
    }

    // ── The auto-fit + orphan banners follow their VM flags (rule 33) ──

    [AvaloniaFact]
    public void AutoFitAndOrphanBanners_FollowTheirFlags()
    {
        // Neither flag set → both banners hidden.
        using VmHarness plain = new();
        plain.Vm.CurrentStep = 2; // clear _summaryDirty before seeding (see the render test's note)
        plain.Vm.Summaries.Add(MakeSummary("Catbox", "me", available: null, ("clip.mkv", 4096, true)));
        (Window plainWindow, UploadWizardWindow plainWizard) = Show(plain.Vm);

        // Both flags pre-seeded (after the step-2 rebuild) before Show → both banners visible at bind time.
        using VmHarness flagged = new();
        flagged.Vm.CurrentStep = 2; // clear _summaryDirty before seeding (see the render test's note)
        flagged.Vm.Summaries.Add(MakeSummary("Catbox", "me", available: null, ("clip.mkv", 4096, true)));
        flagged.Vm.AutoFitNotice = "2 files unchecked to fit the available space.";
        flagged.Vm.OrphanFiles.Add(new FileEntry { FileName = "toobig.iso", RelativePath = "toobig.iso", Size = 999_999 });
        (Window flaggedWindow, UploadWizardWindow flaggedWizard) = Show(flagged.Vm);
        try
        {
            Assert.False(plainWizard.AutoFitBanner.IsVisible);
            Assert.False(plainWizard.OrphanBanner.IsVisible);

            Assert.True(flaggedWizard.AutoFitBanner.IsVisible);
            Assert.True(flaggedWizard.OrphanBanner.IsVisible);
        }
        finally
        {
            plainWindow.Close();
            flaggedWindow.Close();
        }
    }

    // ── helpers ──

    private static HosterUploadSummary MakeSummary(
        string hoster, string account, long? available, params (string Name, long Size, bool Included)[] files)
    {
        List<SummaryFileItem> items = files
            .Select(f => new SummaryFileItem(
                new FileEntry { FileName = f.Name, RelativePath = f.Name, Size = f.Size }, f.Included))
            .ToList();
        return new HosterUploadSummary(hoster, account, items, available, maxFileSize: null);
    }

    private static List<Expander> Expanders(UploadWizardWindow wizard)
        => wizard.SummariesList.GetVisualDescendants().OfType<Expander>().ToList();

    private static Expander ExpanderFor(UploadWizardWindow wizard, HosterUploadSummary summary)
        => Expanders(wizard).First(e => ReferenceEquals(e.DataContext, summary));

    private static ContentPresenter BodyPresenter(Expander expander)
        => expander.GetVisualDescendants().OfType<ContentPresenter>()
            .First(c => c.Name == "PART_ContentPresenter");

    private static (Window Window, UploadWizardWindow Wizard) Show(UploadWizardViewModel vm)
    {
        UploadWizardWindow wizard = new(vm);
        wizard.Show();
        Dispatcher.UIThread.RunJobs();
        return (wizard, wizard);
    }

    /// <summary>
    /// A real <see cref="UploadWizardViewModel"/> over an in-memory SQLite DB — the same scratch-repo harness the
    /// other wizard suites use. The Summary is populated by adding constructed <see cref="HosterUploadSummary"/>
    /// items to <see cref="UploadWizardViewModel.Summaries"/> directly, never through GoNext.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UploadScheduler _scheduler;

        public VmHarness()
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

            Vm = new UploadWizardViewModel(packageManager, loginRepo, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), settings);
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
