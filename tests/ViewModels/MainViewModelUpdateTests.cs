// <copyright file="MainViewModelUpdateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.ViewModels;

[Collection(LocalizerCollection.Name)]
public class MainViewModelUpdateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly CultureInfo _originalCulture;
    private readonly List<MainViewModel> _vms = [];

    public MainViewModelUpdateTests()
    {
        // Pin Localizer to English — WindowTitle assertions ("Update available", "CSUploader")
        // hit Localizer.Instance, so we'd flake on any non-en host or after a peer test that
        // mutated the singleton.
        _originalCulture = Localizer.Instance.Culture;
        Localizer.Instance.Culture = new CultureInfo("en");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        ServiceCollection sc = new();
        sc.AddSingleton(Mock.Of<IAppLogger>());
        sc.AddSingleton(new AppSettings());
        sc.AddDbContextFactory<CSUploaderDbContext>(o => o.UseSqlite(_connection));
        sc.AddSingleton<SettingRepository>();
        sc.AddSingleton<FileHosterLoginRepository>();
        sc.AddSingleton<UploadPackageRepository>();
        sc.AddSingleton<UploadPackageFileRepository>();
        sc.AddSingleton<ProxySettingRepository>();
        sc.AddSingleton<IProxySource>(sp => sp.GetRequiredService<ProxyManager>());
        sc.AddSingleton<IHttpHandlerFactory>(sp => new DefaultHttpHandlerFactory(sp.GetRequiredService<AppSettings>()));
        sc.AddSingleton<IFileHosterRegistry>(new DefaultFileHosterRegistry([]));
        sc.AddSingleton<AttemptRunner>();
        sc.AddSingleton<CSUploader.Lib.Crypto.IHashingService, CSUploader.Lib.Crypto.HashingService>();
        sc.AddSingleton<UploadScheduler>();
        sc.AddSingleton<PackageManager>();
        sc.AddSingleton<ProxyManager>();
        sc.AddSingleton(Mock.Of<IDialogService>());
        sc.AddSingleton(Mock.Of<IAccountVerifier>());
        sc.AddSingleton<IUiDispatcher, InlineUiDispatcher>();
        sc.AddSingleton(Mock.Of<IClipboardService>());
        sc.AddSingleton(Mock.Of<IToastNotificationService>());
        sc.AddSingleton<UploadsViewModel>();
        sc.AddSingleton<UploadedViewModel>();
        sc.AddSingleton<AccountManagerViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<ConnectionManagerViewModel>();
        sc.AddSingleton<LogsViewModel>();

        _services = sc.BuildServiceProvider();

        using CSUploaderDbContext db = _services.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // MainViewModel is IDisposable (Phase 9 ledger fix c): dispose each built VM so it detaches its
        // process-global Localizer/logger subscriptions and stops its update timer, rather than leaking
        // dead subscribers onto the singleton across the run.
        foreach (MainViewModel vm in _vms)
        {
            vm.Dispose();
        }

        _services.Dispose();
        _connection.Dispose();
        Localizer.Instance.Culture = _originalCulture;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenNoUpdate_LeavesIsUpdateAvailableFalse()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Null(vm.AvailableVersion);
        Assert.Equal("CSUploader", vm.WindowTitle);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenUpdateAvailable_SetsFlagsAndTitle()
    {
        UpdateAvailableInfo info = new("2.3.4", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("2.3.4", vm.AvailableVersion);
        Assert.Contains("Update available", vm.WindowTitle, StringComparison.Ordinal);
        Assert.Contains("2.3.4", vm.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenServiceThrows_DoesNotCrash()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("network down"));
        MainViewModel vm = CreateVm(updater.Object);

        // Should swallow the exception and just leave the VM in the no-update state.
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task InstallUpdateCommand_WhenNoUpdate_CannotExecute()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallUpdateCommand_WhenUpdateAvailable_CanExecute()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
    }

    /// <summary>
    /// A loose build's "there is a newer version" is NOT an offer to install it. Nothing can be
    /// installed without a Velopack layout, so arming the command would put a button on screen whose
    /// only reachable outcome is <c>NotInstalledException</c>.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatCannotBeInstalledArmsNothing()
    {
        Mock<IUpdateService> updater = new();
        updater
            .Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.AvailableNotInstallable("9.9.9"));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Null(vm.AvailableVersion);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    /// <summary>
    /// The clearing has to reach the private payload, not just the flags. <c>ExecuteAsync</c> runs
    /// the command body whether or not <c>CanExecute</c> agrees, so a stale <c>_availableUpdate</c>
    /// left behind by an earlier installable check would still be installable by that route.
    /// </summary>
    [Fact]
    public async Task AnUninstallableCheckDisarmsAnInstallableOneBeforeIt()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(true);
        updater
            .SetupSequence(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.Available(info))
            .ReturnsAsync(UpdateCheckResult.AvailableNotInstallable("9.9.9"));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        updater.Verify(
            u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        updater.Verify(u => u.ApplyAndRestart(It.IsAny<UpdateAvailableInfo>()), Times.Never);
    }

    /// <summary>
    /// The second half of the same guard, reached the other way round: an update that IS installable
    /// in principle, in a process that turns out not to be installed. Velopack's download opens with
    /// <c>EnsureInstalled</c>, so going ahead would open a progress window purely to show a throw.
    /// </summary>
    [Fact]
    public async Task InstallingIsRefusedWhenTheProcessIsNotInstalled()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(false);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        FakeUpdateProgressSink sink = new();
        MainViewModel vm = CreateVm(updater.Object, sink);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        updater.Verify(
            u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, sink.OpenCount);
    }

    [Fact]
    public async Task InstallUpdateCommand_DrivesUpdateProgressSink()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(true);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        updater
            .Setup(u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateAvailableInfo _, IProgress<int>? p, CancellationToken _) =>
            {
                p?.Report(100);
                return Task.CompletedTask;
            });

        FakeUpdateProgressSink sink = new();
        MainViewModel vm = CreateVm(updater.Object, sink);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        // The window is shown once, entirely through the sink — the VM no longer touches any Window.
        Assert.Equal(1, sink.OpenCount);

        // Two status transitions on the success path: "downloading v9.9.9" then "restarting"
        // (the mock's ApplyAndRestart is a no-op, so no real restart occurs).
        Assert.True(sink.Statuses.Count >= 2);
        Assert.Contains(sink.Statuses, s => s.Contains("9.9.9", StringComparison.Ordinal));

        // Progress pumps through the sink. Report arrives via Progress<int>, which marshals off the
        // captured synchronization context onto the thread pool, so wait rather than assert inline.
        Assert.True(sink.WaitForAnyReport(TimeSpan.FromSeconds(5)));
        Assert.Contains(100, sink.Percents);

        // The WPF install flow never programmatically closes the window (success restarts the
        // process; failure leaves the error visible), so the sink is never Closed. This asserts the
        // behavior was preserved by the reroute.
        Assert.Equal(0, sink.CloseCount);
    }

    /// <summary>
    /// The join the sink tests cannot reach: the size the update advertised has to arrive at the
    /// window. Drop it — construct the stats with 0 instead of <c>DownloadBytes</c> — and every
    /// other test here stays green while the byte readout silently disappears from the real window.
    /// </summary>
    [Fact]
    public async Task InstallUpdateCommand_FeedsTheAdvertisedSizeIntoTheProgress()
    {
        const long Advertised = 71_303_168;
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Full(Advertised));
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(true);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        updater
            .Setup(u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateAvailableInfo _, IProgress<int>? p, CancellationToken _) =>
            {
                p?.Report(0);
                p?.Report(50);
                return Task.CompletedTask;
            });

        FakeUpdateProgressSink sink = new();
        MainViewModel vm = CreateVm(updater.Object, sink);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.True(sink.WaitForAnyReport(TimeSpan.FromSeconds(5)));

        UpdateDownloadProgress last = await sink.WaitForPercentAsync(50, TimeSpan.FromSeconds(5));
        Assert.Equal(Advertised, last.TotalBytes);
        Assert.Equal(Advertised / 2, last.BytesReceived);
    }



    private sealed class FakePrompt(StartupUpdatePromptResult answer) : IStartupUpdatePrompt
    {
        public int Shown { get; private set; }

        public bool AskedWith { get; private set; }

        public Task<StartupUpdatePromptResult> ShowAsync(string newVersion, string currentVersion, bool checkAtStartup)
        {
            Shown++;
            AskedWith = checkAtStartup;
            return Task.FromResult(answer);
        }
    }

    private MainViewModel GateVm(UpdateCheckResult result, FakePrompt prompt, out StartupGate gate)
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), prompt: prompt);
        gate = new StartupGate(TimeSpan.FromSeconds(5), default);
        vm.StartupGate = gate;
        return vm;
    }

    /// <summary>
    /// The ordinary find: the gate releases the main window, waits for the head to confirm the swap,
    /// and only THEN asks. Asking earlier would own the prompt to a splash that is about to close,
    /// taking the prompt with it.
    /// </summary>
    [Fact]
    public async Task WhenAnUpdateIsFound_ItAsksAfterTheMainWindowIsUp()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = GateVm(UpdateCheckResult.Available(info), prompt, out StartupGate gate);

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;

        Assert.Equal(0, prompt.Shown); // not yet: the window it belongs to does not exist
        gate.MarkMainWindowReady();
        await gating;

        Assert.Equal(1, prompt.Shown);
    }

    /// <summary>
    /// A gated startup that turns out to be opted OUT installs nothing and asks nothing.
    /// </summary>
    /// <remarks>
    /// The head and this code read the preference at different moments and can disagree.
    /// StartupUpdatePreference answers "unknown" for a database it could not read - locked by another
    /// process, mid-migration - and the head treats unknown as "gate", which is the right default for
    /// showing a splash and no authority at all for installing something. By the time the gate
    /// finishes, hydration has loaded the real value, and it can say the owner turned startup checks
    /// off while auto-install sits switched on underneath a greyed-out box.
    /// <para>
    /// Without the re-check that pairing means an unannounced restart, decided by a transient file
    /// lock rather than by the user. The update is still published - the title bar reports it - it is
    /// only the acting on it that stops.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGatedStartupThatIsActuallyOptedOut_NeitherAsksNorInstalls()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        FakePrompt prompt = new(new StartupUpdatePromptResult(true, true)); // would say "Update now"
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(true);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));

        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), prompt: prompt);

        // The disagreement: the head gated (it had to guess), hydration says otherwise.
        vm.SettingsViewModel.CheckForUpdatesAtStartup = false;
        vm.SettingsViewModel.AutoInstallUpdatesAtStartup = true;
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);
        vm.StartupGate = gate;

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.MarkMainWindowReady();
        await gating;

        Assert.Equal(0, prompt.Shown);
        updater.Verify(
            u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        updater.Verify(u => u.ApplyAndRestart(It.IsAny<UpdateAvailableInfo>()), Times.Never);

        Assert.True(vm.IsUpdateAvailable); // reported, just not acted on
    }

    /// <summary>
    /// The two halves of the split setting. "Check for updates at startup" decides that the check
    /// runs in front of the window rather than behind it - an answer by the time it opens if one
    /// arrives inside the deadline; "install updates automatically at startup" decides what happens
    /// to that answer. On, the update installs and no prompt is ever constructed. Off, the user is
    /// asked and nothing installs itself behind them.
    /// </summary>
    /// <remarks>
    /// Asserted as one theory rather than two tests because the risk is not either branch in
    /// isolation — it is the flag being ignored, which looks identical to whichever branch was
    /// hard-coded. Only running both ways can tell that apart.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoInstallDecidesWhetherTheStartupUpdateAsksFirst(bool autoInstall)
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true)); // "Later", if asked at all
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.IsInstalled).Returns(true);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));

        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), prompt: prompt);
        vm.SettingsViewModel.AutoInstallUpdatesAtStartup = autoInstall;
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);
        vm.StartupGate = gate;

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.MarkMainWindowReady();
        await gating;

        Assert.Equal(autoInstall ? 0 : 1, prompt.Shown);
        updater.Verify(
            u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()),
            autoInstall ? Times.Once() : Times.Never());
        updater.Verify(u => u.ApplyAndRestart(It.IsAny<UpdateAvailableInfo>()), autoInstall ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task WhenThereIsNoUpdate_ItReleasesTheWindowAndAsksNothing()
    {
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = GateVm(UpdateCheckResult.UpToDate, prompt, out StartupGate gate);

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.MarkMainWindowReady();
        await gating;

        Assert.Equal(0, prompt.Shown);
    }

    /// <summary>A failed check is not a reason to interrupt anyone: no prompt, window still released.</summary>
    [Fact]
    public async Task WhenTheCheckFails_ItReleasesTheWindowAndAsksNothing()
    {
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = GateVm(UpdateCheckResult.Failed("offline"), prompt, out StartupGate gate);

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.MarkMainWindowReady();
        await gating;

        Assert.Equal(0, prompt.Shown);
    }

    /// <summary>
    /// The deadline stops GATING, not the check. A slow answer must not hold the main window back —
    /// and having missed its window it must not interrupt later either, because by then the user is
    /// working.
    /// </summary>
    [Fact]
    public async Task WhenTheCheckOutlastsTheDeadline_TheWindowIsReleasedAndNothingIsAsked()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IUpdateService> updater = new();
        updater
            .Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await release.Task;
                return UpdateCheckResult.Available(info);
            });

        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), prompt: prompt);
        StartupGate gate = new(TimeSpan.FromMilliseconds(50), default);
        vm.StartupGate = gate;

        Task gating = vm.RunStartupGateAsync();

        // Bounded: without a deadline the gate waits on the check forever, and a test that HANGS
        // says less than one that fails.
        await gate.MainWindowMayShow.WaitAsync(TimeSpan.FromSeconds(10));
        gate.MarkMainWindowReady();
        await gating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, prompt.Shown);

        // ...and the abandoned check still publishes, so the menu item lights up.
        release.SetResult();
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
        Assert.True(vm.IsUpdateAvailable);
    }

    /// <summary>
    /// A service that throws must not be the reason the app never appears.
    /// <para>
    /// What carries this is the check pipeline NORMALISING the exception into a failed result, not
    /// the gate's own catch — which is unreachable for exactly that reason, and which this test
    /// therefore does not cover.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenTheServiceThrows_TheWindowIsStillReleased()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), prompt: prompt);
        StartupGate gate = new(TimeSpan.FromSeconds(5), default);
        vm.StartupGate = gate;

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow.WaitAsync(TimeSpan.FromSeconds(10));
        gate.MarkMainWindowReady();
        await gating.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, prompt.Shown);
    }

    /// <summary>
    /// The splash being closed is terminal. Initialisation stops rather than waiting for a swap that
    /// will never come, and asks nothing of a user who has quit.
    /// </summary>
    [Fact]
    public async Task WhenTheSplashIsAbandoned_TheGateStopsRatherThanWaiting()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, true));
        MainViewModel vm = GateVm(UpdateCheckResult.Available(info), prompt, out StartupGate gate);

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.Abandon();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gating);
        Assert.Equal(0, prompt.Shown);
    }

    /// <summary>
    /// Unticking the box is persisted, and persisted BEFORE any install begins: Velopack exits the
    /// process, so a write left in flight is a write that never happened.
    /// </summary>
    [Fact]
    public async Task WhenTheUserUnticksTheBox_ThePreferenceIsSaved()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        FakePrompt prompt = new(new StartupUpdatePromptResult(false, false));
        MainViewModel vm = GateVm(UpdateCheckResult.Available(info), prompt, out StartupGate gate);

        Task gating = vm.RunStartupGateAsync();
        await gate.MainWindowMayShow;
        gate.MarkMainWindowReady();
        await gating;

        Assert.True(prompt.AskedWith); // it opened showing the current preference
        Assert.False(vm.SettingsViewModel.CheckForUpdatesAtStartup);
    }

    /// <summary>
    /// The re-entrancy the cached task has to survive, reproduced rather than reasoned about.
    /// <para>
    /// Initialization runs <c>FirstRun.InitializeDatabase</c> before its first await, and that logs
    /// — synchronously, to subscribers that run synchronously. So a subscriber can call back into
    /// <c>InitializeAsync</c> while the first call has not returned. If the cached task were
    /// published only after the work was under way, that re-entrant caller would find the field
    /// null and start a SECOND initialization. Not merely twice, either: that one logs too, so it
    /// re-enters again, and the recursion does not terminate — restoring the bug makes this test
    /// abort the whole test host rather than fail. Publishing the task before any work begins is
    /// what closes it, and a lock cannot: it is re-entrant on the calling thread.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ReEnteredFromItsOwnLogging_StillRunsOnce()
    {
        Logger realLogger = new();
        MainViewModel vm = CreateVm(Mock.Of<IUpdateService>(), new FakeUpdateProgressSink(), logger: realLogger);

        Task? reentrant = null;
        void OnLog(object? sender, LogEvent e) => reentrant ??= vm.InitializeAsync();

        realLogger.OnLogOutput += OnLog;
        try
        {
            Task outer = vm.InitializeAsync();
            await Record.ExceptionAsync(() => outer); // the partial fixture faults it; that is fine

            Assert.NotNull(reentrant);
            Assert.Same(outer, reentrant);
        }
        finally
        {
            realLogger.OnLogOutput -= OnLog;
        }
    }

    /// <summary>
    /// The guard used to be a bool set before the first await, so a SECOND caller returned
    /// immediately while the first was still loading — reporting "initialised" for a database that
    /// was not there yet. It went unnoticed while only <c>MainWindow.Opened</c> called it; the
    /// startup gate adds a second caller, and this is the test that says they share one task.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_OverlappingCallers_ShareOneTask()
    {
        MainViewModel vm = CreateVm(Mock.Of<IUpdateService>(), new FakeUpdateProgressSink());

        Task first = vm.InitializeAsync();
        Task second = vm.InitializeAsync();

        // Reference identity, not merely "both completed": two DISTINCT tasks that each ran the body
        // would also both complete, and would have double-loaded everything on the way.
        Assert.Same(first, second);
        Assert.Same(first, vm.InitializeAsync());

        // ...and every caller observes the SAME outcome. This fixture's provider is deliberately
        // partial, so the body faults - which is the more interesting half to pin: a second caller
        // must see the first's failure rather than a silent success, which is exactly what the old
        // bool guard did.
        Exception? firstOutcome = await Record.ExceptionAsync(() => first);
        Exception? secondOutcome = await Record.ExceptionAsync(() => second);
        Assert.NotNull(firstOutcome); // otherwise Assert.Same(null, null) would pass vacuously
        Assert.Same(firstOutcome, secondOutcome);
    }

    /// <summary>
    /// A startup check must not raise the background toast, whichever of the two startup checks it
    /// is. Behind the splash there is nowhere to put one - the real window does not exist yet, so it
    /// would be orphaned or hidden. In the quiet post-startup case the window does exist, and the
    /// reason holds anyway: that user asked not to be interrupted at startup. Either way they find
    /// out the ordinary way, by the menu item staying disabled.
    /// </summary>
    [Fact]
    public async Task AFailedStartupCheck_IsSilent()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("offline"));
        Mock<IToastNotificationService> toasts = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>...while the six-hourly poll still surfaces one, which is the behaviour that existed
    /// before the startup origin was added and must not have been broken by adding it.</summary>
    [Fact]
    public async Task AFailedPeriodicCheck_StillRaisesTheToast()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("offline"));
        Mock<IToastNotificationService> toasts = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// The check pipeline has the same synchronous re-entrancy hole initialization had, and for the
    /// same reason: publishing an available update LOGS, synchronously, to subscribers that run
    /// synchronously. With a mocked service and an inline dispatcher the whole check can complete
    /// before <c>RunCheckAsync</c> returns — so assigning its result to the in-flight field would
    /// publish too late, and a subscriber calling back in would start another check, and another.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_ReEnteredFromItsOwnLogging_StillRunsOnce()
    {
        Logger realLogger = new();
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), logger: realLogger);

        Task<UpdateCheckResult>? reentrant = null;
        void OnLog(object? sender, LogEvent e) => reentrant ??= vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        realLogger.OnLogOutput += OnLog;
        try
        {
            Task<UpdateCheckResult> outer = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
            await outer;

            Assert.NotNull(reentrant);
            Assert.Same(outer, reentrant);
            updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            realLogger.OnLogOutput -= OnLog;
        }
    }

    /// <summary>
    /// Reporting obligations accumulate. A periodic poll joining a silent startup check must still
    /// get its toast, or a startup check that happens to be in flight silences the poll that was
    /// supposed to tell the user.
    /// </summary>
    [Fact]
    public async Task APeriodicCheckJoiningASilentStartupOne_StillGetsItsToast()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IUpdateService> updater = new();
        updater
            .Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await release.Task;
                return UpdateCheckResult.Failed("offline");
            });

        Mock<IToastNotificationService> toasts = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts);

        Task<UpdateCheckResult> startup = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
        Task<UpdateCheckResult> periodic = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        release.SetResult();
        await Task.WhenAll(startup, periodic);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Holds <c>InvokeAsync</c> open so a test can stand between the network result and its
    /// publication. <c>DeferredUiDispatcher</c> defers <c>Post</c> and runs <c>InvokeAsync</c>
    /// inline, and the update pipeline publishes through the latter.
    /// </summary>
    private sealed class GatedInvokeDispatcher : CSUploader.Services.IUiDispatcher
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public void Post(Action action) => action();

        public async Task InvokeAsync(Action action)
        {
            _entered.TrySetResult();
            await _release.Task;
            action();
        }

        public CSUploader.Services.IUiTimer CreateTimer(TimeSpan interval, Action onTick)
            => new InlineUiDispatcher.TestTimer(onTick);
    }

    /// <summary>
    /// The boundary the earlier snapshot-before-publication got wrong. A poll can join AFTER the
    /// network call has returned but BEFORE the result reaches the UI thread — the shared task is
    /// still incomplete, so joining is legal — and a reporting decision taken before that hop would
    /// have dropped the toast it was owed.
    /// </summary>
    [Fact]
    public async Task APeriodicCheckJoiningDuringPublication_StillGetsItsToast()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("offline"));
        Mock<IToastNotificationService> toasts = new();

        GatedInvokeDispatcher dispatcher = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts, dispatcher: dispatcher);

        Task<UpdateCheckResult> startup = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
        await dispatcher.Entered; // the network call is done; publication has not run

        Task<UpdateCheckResult> periodic = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        dispatcher.Release();
        await Task.WhenAll(startup, periodic);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// The other direction, which a visibility RANKING got backwards: a user joining a periodic
    /// check must not cancel the poll's toast. The user gets their answer from the returned result;
    /// the poll's promise is separate and still owed.
    /// </summary>
    [Fact]
    public async Task AUserCheckJoiningAPeriodicOne_DoesNotCancelItsToast()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IUpdateService> updater = new();
        updater
            .Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await release.Task;
                return UpdateCheckResult.Failed("offline");
            });

        Mock<IToastNotificationService> toasts = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts);

        Task<UpdateCheckResult> periodic = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        Task<UpdateCheckResult> user = vm.CheckForUpdatesAsync(UpdateCheckOrigin.User);
        release.SetResult();
        await Task.WhenAll(periodic, user);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// A user check on its own stays quiet: the menu renders the outcome, and a toast as well would
    /// be a second answer to one question.
    /// </summary>
    [Fact]
    public async Task AFailedUserCheck_RaisesNoToast()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("offline"));
        Mock<IToastNotificationService> toasts = new();
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink(), toasts);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.User);

        toasts.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A caller arriving while a check is running JOINS it instead of queueing behind it. That is
    /// what keeps a user pressing Check for Updates from waiting out a startup check that has
    /// already outlived its deadline — Velopack offers no way to cancel one, so queueing would mean
    /// the dialog hangs for as long as the abandoned request takes.
    /// </summary>
    [Fact]
    public async Task ASecondCheckDuringOne_JoinsItRatherThanQueueing()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Mock<IUpdateService> updater = new();
        updater
            .Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return UpdateCheckResult.Available(info);
            });

        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink());

        Task<UpdateCheckResult> startup = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
        Task<UpdateCheckResult> user = vm.CheckForUpdatesAsync(UpdateCheckOrigin.User);

        // The same task, not two: the second caller is not merely served eventually, it is served by
        // the first caller's work. Queueing would give two distinct tasks and two network calls.
        Assert.Same(startup, user);

        release.SetResult();
        await Task.WhenAll(startup, user);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(vm.IsUpdateAvailable);
    }

    /// <summary>...and once it has finished, the next caller starts a fresh one rather than being
    /// handed the stale completed task forever.</summary>
    [Fact]
    public async Task AfterACheckCompletes_TheNextCallerStartsAFreshOne()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        MainViewModel vm = CreateVm(updater.Object, new FakeUpdateProgressSink());

        Task<UpdateCheckResult> first = vm.CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
        await first;
        Task<UpdateCheckResult> second = vm.CheckForUpdatesAsync(UpdateCheckOrigin.User);
        await second;

        Assert.NotSame(first, second);
        updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_BackgroundFailure_ShowsToastOnce()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("network down"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic); // background
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic); // still failing → debounced, no second toast

        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_BackgroundFailure_ThenSuccess_ReArmsToast()
    {
        Mock<IUpdateService> updater = new();
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("down"));
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic); // success re-arms
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("down again"));
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UserInitiatedFailure_NoToast_ReturnsFailed()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("boom"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        UpdateCheckResult result = await vm.CheckForUpdatesAsync(UpdateCheckOrigin.User);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("boom", result.FailureReason);
        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FailureAfterAvailable_KeepsUpdateAvailable()
    {
        UpdateAvailableInfo info = new("2.3.4", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
        MainViewModel vm = CreateVm(updater.Object);

        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("blip"));
        await vm.CheckForUpdatesAsync(UpdateCheckOrigin.Periodic);

        Assert.True(vm.IsUpdateAvailable); // a transient failure must not hide a known update
        Assert.Equal("2.3.4", vm.AvailableVersion);
    }

    private MainViewModel CreateVm(
        IUpdateService updater,
        IUpdateProgressSink? sink = null,
        Mock<IToastNotificationService>? toast = null,
        IAppLogger? logger = null,
        CSUploader.Services.IUiDispatcher? dispatcher = null,
        IStartupUpdatePrompt? prompt = null)
    {
        // Re-register the update service for this test. The service provider was built
        // without one, so we wrap it in a small composite that overrides that single key.
        ServiceProvider scoped = BuildScopedProvider(
            updater,
            sink ?? Mock.Of<IUpdateProgressSink>(),
            (toast ?? new Mock<IToastNotificationService>()).Object,
            logger,
            dispatcher,
            prompt);
        MainViewModel vm = new(scoped);
        _vms.Add(vm); // disposed at teardown (MainViewModel is IDisposable — Phase 9 ledger fix c).
        return vm;
    }

    private ServiceProvider BuildScopedProvider(
        IUpdateService updater,
        IUpdateProgressSink sink,
        IToastNotificationService toast,
        IAppLogger? logger = null,
        CSUploader.Services.IUiDispatcher? dispatcher = null,
        IStartupUpdatePrompt? prompt = null)
    {
        ServiceCollection sc = new();
        if (prompt is not null)
        {
            sc.AddSingleton(prompt);
        }


        // The fixture's logger is Mock.Of<IAppLogger>(), whose Log is a no-op and whose OnLogOutput
        // therefore never fires. One test needs a REAL logger, because the behaviour it pins is
        // re-entrancy THROUGH a synchronous log callback.
        sc.AddSingleton(logger ?? _services.GetRequiredService<IAppLogger>());
        sc.AddSingleton(_services.GetRequiredService<AppSettings>());
        sc.AddSingleton(_services.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>());
        sc.AddSingleton(_services.GetRequiredService<SettingRepository>());
        sc.AddSingleton(_services.GetRequiredService<FileHosterLoginRepository>());
        sc.AddSingleton(_services.GetRequiredService<UploadPackageRepository>());
        sc.AddSingleton(_services.GetRequiredService<UploadPackageFileRepository>());
        sc.AddSingleton(_services.GetRequiredService<ProxySettingRepository>());
        sc.AddSingleton(_services.GetRequiredService<UploadScheduler>());
        sc.AddSingleton(_services.GetRequiredService<PackageManager>());
        sc.AddSingleton(_services.GetRequiredService<ProxyManager>());
        sc.AddSingleton(_services.GetRequiredService<IDialogService>());
        sc.AddSingleton(_services.GetRequiredService<IAccountVerifier>());
        sc.AddSingleton(dispatcher ?? _services.GetRequiredService<IUiDispatcher>());
        sc.AddSingleton(_services.GetRequiredService<UploadsViewModel>());
        sc.AddSingleton(_services.GetRequiredService<UploadedViewModel>());
        sc.AddSingleton(_services.GetRequiredService<SettingsViewModel>());
        sc.AddSingleton(_services.GetRequiredService<ConnectionManagerViewModel>());
        sc.AddSingleton(_services.GetRequiredService<LogsViewModel>());
        sc.AddSingleton(updater);
        sc.AddSingleton(sink);
        sc.AddSingleton(toast);
        return sc.BuildServiceProvider();
    }

    /// <summary>
    /// Records the update-progress sink's calls so the install flow can be asserted without a
    /// real <c>UpdateProgressWindow</c>. Thread-safe because <see cref="IProgress{T}"/> marshals
    /// <see cref="Report"/> off the captured synchronization context onto the thread pool.
    /// </summary>
    private sealed class FakeUpdateProgressSink : IUpdateProgressSink
    {
        private readonly ManualResetEventSlim _reported = new();
        private readonly object _gate = new();
        private readonly List<UpdateDownloadProgress> _reports = [];
        private readonly List<string> _statuses = [];

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public IReadOnlyList<UpdateDownloadProgress> Reports
        {
            get { lock (_gate) { return [.. _reports]; } }
        }

        /// <summary>Just the percentages, for the assertions that only care about those.</summary>
        public IReadOnlyList<int> Percents
        {
            get { lock (_gate) { return [.. _reports.Select(r => r.Percent)]; } }
        }

        public IReadOnlyList<string> Statuses
        {
            get { lock (_gate) { return [.. _statuses]; } }
        }

        public void Open()
        {
            lock (_gate) { OpenCount++; }
        }

        public void SetStatus(string status)
        {
            lock (_gate) { _statuses.Add(status); }
        }

        public void Report(UpdateDownloadProgress progress)
        {
            lock (_gate) { _reports.Add(progress); }
            _reported.Set();
        }

        public void Close()
        {
            lock (_gate) { CloseCount++; }
        }

        public bool WaitForAnyReport(TimeSpan timeout) => _reported.Wait(timeout);

        /// <summary>
        /// Waits for the tick carrying <paramref name="percent"/>. Progress&lt;int&gt; posts every
        /// report to the thread pool, so the last one can still be in flight when the command's task
        /// completes — asserting on the list right away is a race that passes on a fast machine.
        /// </summary>
        public async Task<UpdateDownloadProgress> WaitForPercentAsync(int percent, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    int index = _reports.FindIndex(r => r.Percent == percent);
                    if (index >= 0)
                    {
                        return _reports[index];
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"no report at {percent}% arrived within {timeout}");
        }
    }
}
