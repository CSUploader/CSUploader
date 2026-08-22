// <copyright file="UploadWizardHosterFilterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// The File Hosters step's filter. The list runs to ~80 hosters, so it needs a way in — but the
/// filter is a VIEW concern and must stay one: <see cref="WizardHostersViewModel.FileHosters"/> is
/// what the wizard reads when it builds the upload, so a hoster ticked and then filtered out of
/// sight has to keep uploading.
/// </summary>
public class UploadWizardHosterFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly UploadWizardViewModel _vm;

    // Kept so a test can open a SECOND wizard against different settings — the startup-filter
    // seeding is a construction-time behaviour, so it can't be observed on the shared one.
    private readonly PackageManager _packageManager;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly IDialogService _dialogService = Mock.Of<IDialogService>();

    public UploadWizardHosterFilterTests()
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
        _scheduler = new UploadScheduler(
            settings,
            BuildAttemptRunner(),
            Mock.Of<IAppLogger>(),
            new CSUploader.Lib.Crypto.HashingService(),
            registry);

        PackageManager packageManager = new(
            settings,
            _scheduler,
            new UploadPackageRepository(factory),
            new UploadPackageFileRepository(factory),
            loginRepo,
            Mock.Of<IAppLogger>(),
            registry);

        _packageManager = packageManager;
        _loginRepo = loginRepo;
        _vm = new UploadWizardViewModel(packageManager, loginRepo, _dialogService, Mock.Of<IAppLogger>(), settings);

        // A realistic mix, and deliberately not a tidy one: Catbox does BOTH (anonymous uploads and
        // accounts), which is the case an "account = not anonymous" filter would get wrong. World
        // Files is anonymous with no accounts to offer; the other two are account-only. Names
        // overlap so a substring filter has something to discriminate.
        // Capabilities and captcha verdicts are each hoster's real ones (see the pipelines and
        // docs/hoster-download-captcha.md), so the filters are exercised against the shape the
        // wizard actually builds at runtime.
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "Catbox", [], supportsAnonymous: true, supportsAccounts: true,
            downloadCaptcha: DownloadCaptchaRequirement.NotRequired));
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "World Files", [], supportsAnonymous: true, downloadCaptcha: DownloadCaptchaRequirement.Required));
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "Rapidgator", [Account("Rapidgator")], supportsAccounts: true,
            downloadCaptcha: DownloadCaptchaRequirement.Required));
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "FileCat", [Account("FileCat")], supportsAccounts: true,
            downloadCaptcha: DownloadCaptchaRequirement.Required));
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoFilter_ShowsEverything()
    {
        Assert.All(_vm.Hosters.FileHosters, h => Assert.True(_vm.Hosters.MatchesHosterFilter(h)));
        Assert.False(_vm.Hosters.IsHosterFilterActive);
        Assert.Equal(4, _vm.Hosters.VisibleHosterCount);
    }

    [Theory]
    [InlineData("cat", new[] { "Catbox", "FileCat" })]   // matches anywhere in the name, not just the start
    [InlineData("CAT", new[] { "Catbox", "FileCat" })]   // case-insensitive
    [InlineData("  world  ", new[] { "World Files" })]   // trimmed
    [InlineData("files", new[] { "World Files" })]
    [InlineData("zzz", new string[0])]
    public void NameFilter_MatchesSubstringsCaseInsensitively(string needle, string[] expected)
    {
        _vm.Hosters.HosterFilterText = needle;

        Assert.Equal(expected, Visible());
    }

    [Fact]
    public void AnonymousOnly_KeepsOnlyHostersThatNeedNoAccount()
    {
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;

        Assert.Equal(["Catbox", "World Files"], Visible());
        Assert.True(_vm.Hosters.IsHosterFilterActive);
    }

    [Fact]
    public void AccountOnly_KeepsHostersThatOFFERAccounts_NotMerelyTheNonAnonymousOnes()
    {
        _vm.Hosters.AccountFilter = HosterAccountFilter.AccountOnly;

        // Catbox is the whole point: it takes anonymous uploads AND offers accounts, so it belongs
        // here too. Reading "account only" as "not anonymous" would drop it — and with it catbox,
        // gofile, ufile, upload.ee and UpZur in the real list, which is exactly the mistake
        // IFileHosterPipeline.SupportsAccounts warns about in its own doc comment.
        Assert.Equal(["Catbox", "Rapidgator", "FileCat"], Visible());
        Assert.True(_vm.Hosters.IsHosterFilterActive);
    }

    [Fact]
    public void TheTwoNarrowingModes_OverlapRatherThanPartition()
    {
        // Stated directly, because it is the property the whole enum exists to preserve: a hoster
        // that does both appears under EITHER mode, so the two are not complements.
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        Assert.Contains("Catbox", Visible());

        _vm.Hosters.AccountFilter = HosterAccountFilter.AccountOnly;
        Assert.Contains("Catbox", Visible());

        // …and a host that does only one is in only one.
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        Assert.DoesNotContain("Rapidgator", Visible());
        _vm.Hosters.AccountFilter = HosterAccountFilter.AccountOnly;
        Assert.DoesNotContain("World Files", Visible());
    }

    [Fact]
    public void Both_IsTheNeutralMode_AndDoesNotCountAsFiltering()
    {
        _vm.Hosters.AccountFilter = HosterAccountFilter.Both;

        Assert.Equal(4, _vm.Hosters.VisibleHosterCount);
        Assert.False(_vm.Hosters.IsHosterFilterActive);
    }

    [Fact]
    public void TheTwoFiltersCombine_RatherThanReplaceEachOther()
    {
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        _vm.Hosters.HosterFilterText = "cat";

        // FileCat matches the name but isn't anonymous; Catbox is both.
        Assert.Equal(["Catbox"], Visible());
    }

    [Fact]
    public void NoDownloadCaptchaOnly_KeepsOnlyHostersVerifiedCaptchaFree()
    {
        // The point of the toggle: "show me hosts my downloaders can just download from".
        _vm.Hosters.NoDownloadCaptchaOnly = true;

        Assert.Equal(["Catbox"], Visible());
        Assert.True(_vm.Hosters.IsHosterFilterActive);
    }

    [Fact]
    public void NoDownloadCaptchaOnly_HidesUnverifiedHosters_BecauseADashIsNotANo()
    {
        // The honesty rule the whole column is built on: Unknown means "not verified", never "no
        // captcha". Neither an unverified hoster nor one with no pipeline verdict at all may pass a
        // filter whose promise is that the downloader won't meet a captcha.
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "Xubster", [Account("Xubster")], downloadCaptcha: DownloadCaptchaRequirement.Unknown));
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel("Nowhere", [Account("Nowhere")]));

        _vm.Hosters.NoDownloadCaptchaOnly = true;

        Assert.Equal(["Catbox"], Visible());
    }

    [Fact]
    public void AllThreeFiltersCombine()
    {
        // FileGarden is captcha-free but account-only, so neither toggle can stand in for the
        // other — each has to do its own work for these assertions to hold.
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "FileGarden", [Account("FileGarden")], downloadCaptcha: DownloadCaptchaRequirement.NotRequired));

        _vm.Hosters.NoDownloadCaptchaOnly = true;
        Assert.Equal(["Catbox", "FileGarden"], Visible());

        // Anonymous-only drops FileGarden, which the captcha filter had kept.
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        Assert.Equal(["Catbox"], Visible());

        // …and the name filter still applies on top of both: nothing survives all three.
        _vm.Hosters.HosterFilterText = "world";
        Assert.Empty(Visible());
    }

    [Fact]
    public void TheSummaryCountsWhatIsVisible_OutOfTheWholeList()
    {
        _vm.Hosters.HosterFilterText = "cat";

        Assert.Equal(2, _vm.Hosters.VisibleHosterCount);
        Assert.Contains("2", _vm.Hosters.HosterFilterSummary, StringComparison.Ordinal);
        Assert.Contains("4", _vm.Hosters.HosterFilterSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAnyFilter_RaisesTheInvalidationTheHeadRefreshesOn()
    {
        int raised = 0;
        _vm.Hosters.HosterFilterInvalidated += (_, _) => raised++;

        _vm.Hosters.HosterFilterText = "cat";
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        _vm.Hosters.NoDownloadCaptchaOnly = true;

        Assert.Equal(3, raised);
    }

    [Fact]
    public void ClearingResetsEveryFilter()
    {
        _vm.Hosters.HosterFilterText = "cat";
        _vm.Hosters.AccountFilter = HosterAccountFilter.AccountOnly;
        _vm.Hosters.NoDownloadCaptchaOnly = true;

        _vm.Hosters.ClearHosterFilterCommand.Execute(null);

        Assert.Equal(string.Empty, _vm.Hosters.HosterFilterText);
        Assert.Equal(HosterAccountFilter.Both, _vm.Hosters.AccountFilter);
        Assert.False(_vm.Hosters.NoDownloadCaptchaOnly);
        Assert.False(_vm.Hosters.IsHosterFilterActive);
        Assert.Equal(4, _vm.Hosters.VisibleHosterCount);
    }

    [Fact]
    public void FilteringNeverTouchesTheCollectionTheUploadIsBuiltFrom()
    {
        // The load-bearing one. Tick a hoster, then filter it out of sight: it must still be in
        // FileHosters, still ticked, because that collection — not the grid — is what the wizard
        // reads when it builds the upload.
        FileHosterSelectionViewModel catbox = _vm.Hosters.FileHosters.First(h => h.FileHosterName == "Catbox");
        catbox.Use = true;

        _vm.Hosters.HosterFilterText = "rapid";

        Assert.False(_vm.Hosters.MatchesHosterFilter(catbox));      // hidden from the grid…
        Assert.Equal(4, _vm.Hosters.FileHosters.Count);             // …but still in the list…
        Assert.Contains(catbox, _vm.Hosters.FileHosters);
        Assert.True(catbox.Use);                            // …and still ticked.
    }

    [Fact]
    public void TheSummaryFollowsHostersBeingAdded()
    {
        // The rows arrive one at a time during LoadFileHosters, so the "N of M" has to move with them.
        List<string> changed = [];
        _vm.Hosters.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel("Pixeldrain", [Account("Pixeldrain")]));

        Assert.Equal(5, _vm.Hosters.VisibleHosterCount);
        Assert.Contains(nameof(WizardHostersViewModel.HosterFilterSummary), changed);
    }

    [Fact]
    public void ANonHosterItem_NeverMatches()
    {
        // The predicate is handed whatever the collection view holds; anything else is not a row.
        Assert.False(_vm.Hosters.MatchesHosterFilter("Catbox"));
        Assert.False(_vm.Hosters.MatchesHosterFilter(new object()));
    }

    // ── The Use column's header box ticks everything CURRENTLY LISTED ──

    [Fact]
    public void CheckAll_TicksEveryListedHoster_AndUntickingClearsThem()
    {
        Assert.False(_vm.Hosters.AllListedHostersChecked);   // nothing ticked to begin with

        _vm.Hosters.AllListedHostersChecked = true;

        Assert.All(_vm.Hosters.FileHosters, h => Assert.True(h.Use));
        Assert.True(_vm.Hosters.AllListedHostersChecked);

        _vm.Hosters.AllListedHostersChecked = false;
        Assert.All(_vm.Hosters.FileHosters, h => Assert.False(h.Use));
    }

    [Fact]
    public void CheckAll_ActsOnWhatTheFilterLEAVES_NotTheWholeCatalogue()
    {
        // The entire point of putting it next to a filter: with "Anonymous only" on, check-all
        // should tick the anonymous ones and leave the rest alone.
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;

        _vm.Hosters.AllListedHostersChecked = true;

        Assert.All(_vm.Hosters.FileHosters.Where(h => h.SupportsAnonymous), h => Assert.True(h.Use));
        Assert.All(_vm.Hosters.FileHosters.Where(h => !h.SupportsAnonymous), h => Assert.False(h.Use));
    }

    [Fact]
    public void CheckAll_WithTheCaptchaFilterOn_TicksOnlyTheVerifiedCaptchaFreeRows()
    {
        // The bulk action a captcha-conscious user actually performs: filter to the captcha-free
        // hosts, then tick everything shown. Nothing captcha-gated — or merely unverified — may be
        // swept in, because those rows aren't listed.
        _vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel(
            "Xubster", [Account("Xubster")], downloadCaptcha: DownloadCaptchaRequirement.Unknown));
        _vm.Hosters.NoDownloadCaptchaOnly = true;

        _vm.Hosters.AllListedHostersChecked = true;

        Assert.True(_vm.Hosters.FileHosters.First(h => h.FileHosterName == "Catbox").Use);
        Assert.All(
            _vm.Hosters.FileHosters.Where(h => h.FileHosterName != "Catbox"),
            h => Assert.False(h.Use));
    }

    [Fact]
    public void CheckAll_ReadsAsCheckedWhenTheHiddenRowsAreUnticked()
    {
        // It speaks for the listed rows only, so hosters hidden by the filter must not drag it to
        // partial — otherwise it would never read as fully checked while a filter is on.
        _vm.Hosters.HosterFilterText = "cat";
        _vm.Hosters.AllListedHostersChecked = true;

        Assert.True(_vm.Hosters.AllListedHostersChecked);
        Assert.False(_vm.Hosters.FileHosters.First(h => h.FileHosterName == "Rapidgator").Use);
    }

    [Fact]
    public void CheckAll_IsIndeterminateWhenOnlySomeListedRowsAreTicked()
    {
        _vm.Hosters.FileHosters[0].Use = true;

        Assert.Null(_vm.Hosters.AllListedHostersChecked);
    }

    [Fact]
    public void CheckAll_IgnoresAWriteOfIndeterminate()
    {
        // A three-state box cycles into indeterminate; "make this selection partial" is not an
        // instruction, and acting on it would look like something happened.
        _vm.Hosters.AllListedHostersChecked = true;

        _vm.Hosters.AllListedHostersChecked = null;

        Assert.All(_vm.Hosters.FileHosters, h => Assert.True(h.Use));
    }

    [Fact]
    public void CheckAll_SkipsHostersThatCannotBeUsedAtAll()
    {
        // A hoster with no account and no anonymous route shows a padlock instead of a checkbox;
        // ticking it would be a state the grid cannot show and the upload would drop anyway.
        FileHosterSelectionViewModel blocked = new("Nowhere", []);
        _vm.Hosters.FileHosters.Add(blocked);

        _vm.Hosters.AllListedHostersChecked = true;

        Assert.False(blocked.Use);
        Assert.True(_vm.Hosters.AllListedHostersChecked);   // …and it doesn't hold the header box at partial
    }

    [Fact]
    public void CheckAll_NotifiesWhenARowOrTheFilterChanges()
    {
        List<string> changed = [];
        _vm.Hosters.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _vm.Hosters.FileHosters[0].Use = true;
        Assert.Contains(nameof(WizardHostersViewModel.AllListedHostersChecked), changed);

        changed.Clear();
        _vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
        Assert.Contains(nameof(WizardHostersViewModel.AllListedHostersChecked), changed);
    }

    // ── Next is gated on having picked something, not just on the hosters' declared limits ──

    [Fact]
    public void NextIsBlockedOnTheHostersStep_UntilAtLeastOneIsTicked()
    {
        _vm.CurrentStep = 1;

        // Nothing ticked: the step's whole purpose is unfulfilled, so Next stays off. Before this
        // gate the wizard walked on to a Summary that could only be empty.
        Assert.False(_vm.Hosters.HasSelectedHoster);
        Assert.False(_vm.CanGoNext);

        _vm.Hosters.FileHosters[0].Use = true;

        Assert.True(_vm.Hosters.HasSelectedHoster);
        Assert.True(_vm.CanGoNext);

        // …and back off again when the last tick is removed.
        _vm.Hosters.FileHosters[0].Use = false;
        Assert.False(_vm.CanGoNext);
    }

    [Fact]
    public void TickingAHoster_NotifiesTheTwoPropertiesTheButtonAndHintBindTo()
    {
        // The hint binds the hoster step's HasSelectedHoster; the Next button binds the SHELL's
        // CanGoNext — watching both objects also pins the cross-VM propagation the split introduced
        // (ValidationStateChanged → the shell re-raising CanGoNext).
        _vm.CurrentStep = 1;
        List<string> changed = [];
        _vm.Hosters.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _vm.Hosters.FileHosters[0].Use = true;

        Assert.Contains(nameof(WizardHostersViewModel.HasSelectedHoster), changed);
        Assert.Contains(nameof(UploadWizardViewModel.CanGoNext), changed);
    }

    [Fact]
    public void AHosterTickedThenFilteredOutOfSight_StillSatisfiesTheGate()
    {
        // The gate counts ticks across the whole list, not the filtered view — the same reason the
        // upload itself reads the collection rather than the grid.
        _vm.CurrentStep = 1;
        _vm.Hosters.FileHosters.First(h => h.FileHosterName == "Catbox").Use = true;

        _vm.Hosters.HosterFilterText = "rapid";

        Assert.Equal(["Rapidgator"], Visible());
        Assert.True(_vm.Hosters.HasSelectedHoster);
        Assert.True(_vm.CanGoNext);
    }

    [Fact]
    public void TheGateOnlyAppliesToTheHostersStep()
    {
        // Step 0 (files) and step 3 (start mode) have their own preconditions; a hoster tick is not
        // one of them, and blocking them here would strand the user on the first page.
        foreach (int step in (int[])[0, 3])
        {
            _vm.CurrentStep = step;
            Assert.True(_vm.CanGoNext);
        }
    }

    // ── The wizard opens on the mode the user configured ──

    [Theory]
    [InlineData(HosterAccountFilter.Both)]
    [InlineData(HosterAccountFilter.AnonymousOnly)]
    [InlineData(HosterAccountFilter.AccountOnly)]
    public void TheWizardOpensFilteredToTheConfiguredMode(HosterAccountFilter configured)
    {
        AppSettings settings = new() { WizardHosterAccountFilter = configured };

        UploadWizardViewModel wizard = new(
            _packageManager, _loginRepo, _dialogService, Mock.Of<IAppLogger>(), settings);

        Assert.Equal(configured, wizard.Hosters.AccountFilter);
    }

    [Fact]
    public void ClearReturnsToBoth_EvenWhenTheWizardOpenedNarrowed()
    {
        // Clear means show everything. Returning to the CONFIGURED mode instead would leave rows
        // hidden right after the user asked for the filter to be cleared.
        AppSettings settings = new() { WizardHosterAccountFilter = HosterAccountFilter.AccountOnly };
        UploadWizardViewModel wizard = new(
            _packageManager, _loginRepo, _dialogService, Mock.Of<IAppLogger>(), settings);
        Assert.Equal(HosterAccountFilter.AccountOnly, wizard.Hosters.AccountFilter);

        wizard.Hosters.ClearHosterFilterCommand.Execute(null);

        Assert.Equal(HosterAccountFilter.Both, wizard.Hosters.AccountFilter);
        Assert.False(wizard.Hosters.IsHosterFilterActive);
    }

    [Fact]
    public void TheDropdownOffersExactlyTheThreeModes_InTheOrderTheFilterBarShowsThem()
    {
        Assert.Equal(
            [HosterAccountFilter.Both, HosterAccountFilter.AnonymousOnly, HosterAccountFilter.AccountOnly],
            _vm.Hosters.AccountFilterOptions.Select(o => o.Value));
    }

    private string[] Visible() => [.. _vm.Hosters.FileHosters.Where(_vm.Hosters.MatchesHosterFilter).Select(h => h.FileHosterName)];

    private static FileHosterLoginDto Account(string hoster) => new()
    {
        FileHosterName = hoster,
        Username = "csuprobe",
    };

    /// <summary>A runner the scheduler can hold but never uses here — no upload runs in these tests.</summary>
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
