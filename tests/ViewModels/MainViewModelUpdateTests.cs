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

    [Fact]
    public async Task InstallUpdateCommand_DrivesUpdateProgressSink()
    {
        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        Mock<IUpdateService> updater = new();
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
    /// A startup check must not raise the background toast. The splash is on screen and the main
    /// window does not exist yet, so the toast would be orphaned or hidden behind it — and the user
    /// finds out the ordinary way, by the menu item staying disabled.
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
    /// A shared check reports as visibly as the LOUDEST participant asked for. A periodic poll that
    /// joins a silent startup check must still get its toast — otherwise a startup check that
    /// happens to be in flight silences the poll that was supposed to tell the user.
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
        IAppLogger? logger = null)
    {
        // Re-register the update service for this test. The service provider was built
        // without one, so we wrap it in a small composite that overrides that single key.
        ServiceProvider scoped = BuildScopedProvider(
            updater,
            sink ?? Mock.Of<IUpdateProgressSink>(),
            (toast ?? new Mock<IToastNotificationService>()).Object,
            logger);
        MainViewModel vm = new(scoped);
        _vms.Add(vm); // disposed at teardown (MainViewModel is IDisposable — Phase 9 ledger fix c).
        return vm;
    }

    private ServiceProvider BuildScopedProvider(
        IUpdateService updater,
        IUpdateProgressSink sink,
        IToastNotificationService toast,
        IAppLogger? logger = null)
    {
        ServiceCollection sc = new();

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
        sc.AddSingleton(_services.GetRequiredService<IUiDispatcher>());
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
