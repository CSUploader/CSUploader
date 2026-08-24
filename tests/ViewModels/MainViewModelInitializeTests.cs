// <copyright file="MainViewModelInitializeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
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

/// <summary>
/// Phase 9 ledger fix (b): <see cref="MainViewModel.InitializeAsync"/> is idempotent — a second call is
/// a genuine no-op, not a duplicate load. The Avalonia head re-raises Window.Opened on every tray restore
/// (Hide->Show), which would otherwise re-run the one-time hydration (double-loaded packages, N+1 log
/// persistence). The guard hardens the VM regardless of which head/test path calls it a second time.
/// </summary>
[Collection(LocalizerCollection.Name)]
public class MainViewModelInitializeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly CultureInfo _originalCulture;
    private readonly Mock<IUpdateService> _updater;
    private readonly Mock<IToastNotificationService> _toasts;

    public MainViewModelInitializeTests()
    {
        // MainViewModel's ctor subscribes to the process-global Localizer singleton; pin the culture and
        // serialize via LocalizerCollection so a peer test's culture flip can't perturb this one.
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
        sc.AddSingleton<LogEntryRepository>();
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
        sc.AddSingleton(Mock.Of<IClipboardService>());
        _toasts = new Mock<IToastNotificationService>();
        sc.AddSingleton(_toasts.Object);
        sc.AddSingleton<IUiDispatcher, InlineUiDispatcher>();

        // The ctor resolves these. CheckAsync is stubbed to UpToDate so that when something DOES call
        // it, it is a silent no-op rather than noise in the observed counts. Kept as a field because
        // whether initialization checks at all is a behaviour in its own right - see the
        // InitializeAsync_*CheckAfterStartup / *ChecksNothing pair below.
        _updater = new Mock<IUpdateService>();
        _updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        sc.AddSingleton(_updater.Object);
        sc.AddSingleton(Mock.Of<IUpdateProgressSink>());

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
        _services.Dispose();
        _connection.Dispose();
        Localizer.Instance.Culture = _originalCulture;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_RunsBodyOnce()
    {
        // One persisted log entry. InitializeAsync hydrates the Logs tab by APPENDING each stored row (no
        // clear), so a second run would double it — the cleanest observable of the body re-running. With a
        // Mock IAppLogger (which never raises OnLogOutput), the hydration loop is the ONLY thing that adds
        // to StatusLogs, so the count is fully deterministic.
        await _services.GetRequiredService<LogEntryRepository>().InsertAsync(new LogEntryDto
        {
            DateTime = DateTime.Now,
            LogType = LogType.Status,
            Message = "seed",
        });

        using MainViewModel vm = new(_services); // IDisposable (Phase 9 ledger fix c): detaches its Localizer/logger subs at scope exit.

        await vm.InitializeAsync();
        int afterFirst = vm.LogsViewModel.StatusLogs.Count;
        Assert.Equal(1, afterFirst); // the body ran fully: the seeded entry hydrated exactly once.

        await vm.InitializeAsync(); // second call must be a genuine no-op (idempotency guard).

        Assert.Equal(afterFirst, vm.LogsViewModel.StatusLogs.Count);
    }

    /// <summary>
    /// With "check for updates at startup" off, the check still happens — just behind startup,
    /// where it cannot hold the window back or interrupt anyone.
    /// </summary>
    /// <remarks>
    /// The origin is <c>Startup</c> rather than <c>Periodic</c>, and that is load-bearing: only
    /// <c>Periodic</c> owes a failure toast. Reporting this one as periodic would put an error
    /// notification in front of the very user who asked not to be interrupted at startup.
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_WhenAskedToCheckAfterStartup_ChecksQuietly()
    {
        using MainViewModel vm = new(_services) { CheckForUpdatesAfterStartup = true };
        Assert.Null(vm.StartupGate); // ungated: no splash held anything back

        await vm.InitializeAsync();

        // Fire-and-forget, but CheckForUpdatesAsync reaches the service before its first await, so
        // the call has already been made by the time InitializeAsync returns.
        _updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Without that flag, initialization checks nothing at all.
    /// </summary>
    /// <remarks>
    /// This is <c>--agent</c> and <c>--gallery</c>: ungated like the case above, but not a user who
    /// wants updates - a screenshot loop and a control gallery, which must make no LAUNCH-triggered
    /// request on anyone's behalf. (The six-hourly poll still starts for them; only startup is
    /// silenced.) The check used to be driven by "no gate was set", which cannot tell those two
    /// apart from an owner who merely turned the splash off, so it fired for all three.
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_WithoutThatFlag_ChecksNothing()
    {
        using MainViewModel vm = new(_services);
        Assert.False(vm.CheckForUpdatesAfterStartup); // the default: opt IN, never out

        await vm.InitializeAsync();

        _updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A quiet startup check that FAILS says nothing. This is what pins the origin to
    /// <c>Startup</c>: the same call reported as <c>Periodic</c> would raise a failure toast, and
    /// raising one here would interrupt the very user who asked not to be interrupted at startup.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WhenTheQuietCheckFails_SaysNothing()
    {
        _updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.Failed("no network"));

        using MainViewModel vm = new(_services) { CheckForUpdatesAfterStartup = true };

        await vm.InitializeAsync();

        _updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Once);
        _toasts.Verify(
            t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// The dangerous combination: "install updates automatically at startup" left switched ON while
    /// "check for updates at startup" is OFF. Nothing installs itself.
    /// </summary>
    /// <remarks>
    /// The settings UI greys the auto-install box out rather than hiding it, and deliberately keeps
    /// its stored value so turning the check back on restores the choice. That means this pairing is
    /// reachable and persists - a user can genuinely be sitting on it. What must not follow is a
    /// restart they never agreed to, decided by a box they can no longer reach. Auto-install belongs
    /// to the GATED check; the quiet one only ever reports.
    /// <para>
    /// The preference is written to the store rather than assigned on the view model, because
    /// InitializeAsync hydrates settings on its way through - an assignment here would be overwritten
    /// by the default before the check ever completed, and the test would pass without meaning it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheQuietCheckNeverAutoInstalls()
    {
        await _services.GetRequiredService<SettingRepository>()
            .UpsertAsync(SettingKey.AutoInstallUpdatesAtStartup, "true");
        await _services.GetRequiredService<SettingRepository>()
            .UpsertAsync(SettingKey.CheckForUpdatesAtStartup, "false");

        UpdateAvailableInfo info = new("9.9.9", new object(), UpdateDownloadPlan.Unknown);
        _updater.Setup(u => u.IsInstalled).Returns(true);
        _updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateCheckResult.Available(info));

        using MainViewModel vm = new(_services) { CheckForUpdatesAfterStartup = true };

        await vm.InitializeAsync();

        Assert.True(vm.SettingsViewModel.AutoInstallUpdatesAtStartup); // the setting really is on
        Assert.True(vm.IsUpdateAvailable);                            // and the check really found one
        _updater.Verify(
            u => u.DownloadAsync(It.IsAny<UpdateAvailableInfo>(), It.IsAny<IProgress<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _updater.Verify(u => u.ApplyAndRestart(It.IsAny<UpdateAvailableInfo>()), Times.Never);
    }
}
