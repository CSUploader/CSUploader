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
        sc.AddSingleton(Mock.Of<IToastNotificationService>());
        sc.AddSingleton<IUiDispatcher, InlineUiDispatcher>();

        // The ctor resolves these. CheckAsync is stubbed to UpToDate so that IF anything called it, it
        // would be a silent no-op rather than noise in the observed counts - but nothing should:
        // initialization no longer checks for updates, only the startup gate does. Kept as a field so
        // InitializeAsync_DoesNotCheckForUpdates can hold that to account.
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
    /// Initialization does NOT check for updates. The startup gate is the only thing that does.
    /// </summary>
    /// <remarks>
    /// There used to be a fire-and-forget check here, for the case where no gate was set. Every way of
    /// reaching it is now a case that must not check: <c>--agent</c> and <c>--gallery</c>, which would
    /// put network traffic into the bridge and gallery flows, and an owner who turned "check for
    /// updates when CSUploader starts" off, for whom it did the exact thing the setting promises it
    /// does not. That was survivable only while a non-installed check returned instantly without a
    /// request; it stopped being survivable when the check started reaching GitHub.
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_DoesNotCheckForUpdates()
    {
        using MainViewModel vm = new(_services);
        Assert.Null(vm.StartupGate); // the ungated shape - the one that used to check

        await vm.InitializeAsync();

        _updater.Verify(u => u.CheckAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
