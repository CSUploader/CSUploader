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

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.IsUpdateAvailable);
        Assert.Null(vm.AvailableVersion);
        Assert.Equal("CSUploader", vm.WindowTitle);
        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenUpdateAvailable_SetsFlagsAndTitle()
    {
        UpdateAvailableInfo info = new("2.3.4", new object());
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync();

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
        await vm.CheckForUpdatesAsync();
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task InstallUpdateCommand_WhenNoUpdate_CannotExecute()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallUpdateCommand_WhenUpdateAvailable_CanExecute()
    {
        UpdateAvailableInfo info = new("9.9.9", new object());
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallUpdateCommand_DrivesUpdateProgressSink()
    {
        UpdateAvailableInfo info = new("9.9.9", new object());
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

        await vm.CheckForUpdatesAsync();
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
        Assert.Contains(100, sink.Reports);

        // The WPF install flow never programmatically closes the window (success restarts the
        // process; failure leaves the error visible), so the sink is never Closed. This asserts the
        // behavior was preserved by the reroute.
        Assert.Equal(0, sink.CloseCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_BackgroundFailure_ShowsToastOnce()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("network down"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        await vm.CheckForUpdatesAsync(); // background
        await vm.CheckForUpdatesAsync(); // still failing → debounced, no second toast

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
        await vm.CheckForUpdatesAsync();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        await vm.CheckForUpdatesAsync(); // success re-arms
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("down again"));
        await vm.CheckForUpdatesAsync();

        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UserInitiatedFailure_NoToast_ReturnsFailed()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("boom"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        UpdateCheckResult result = await vm.CheckForUpdatesAsync(userInitiated: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("boom", result.FailureReason);
        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FailureAfterAvailable_KeepsUpdateAvailable()
    {
        UpdateAvailableInfo info = new("2.3.4", new object());
        Mock<IUpdateService> updater = new();
        MainViewModel vm = CreateVm(updater.Object);

        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        await vm.CheckForUpdatesAsync();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("blip"));
        await vm.CheckForUpdatesAsync();

        Assert.True(vm.IsUpdateAvailable); // a transient failure must not hide a known update
        Assert.Equal("2.3.4", vm.AvailableVersion);
    }

    private MainViewModel CreateVm(IUpdateService updater, IUpdateProgressSink? sink = null, Mock<IToastNotificationService>? toast = null)
    {
        // Re-register the update service for this test. The service provider was built
        // without one, so we wrap it in a small composite that overrides that single key.
        ServiceProvider scoped = BuildScopedProvider(
            updater,
            sink ?? Mock.Of<IUpdateProgressSink>(),
            (toast ?? new Mock<IToastNotificationService>()).Object);
        MainViewModel vm = new(scoped);
        _vms.Add(vm); // disposed at teardown (MainViewModel is IDisposable — Phase 9 ledger fix c).
        return vm;
    }

    private ServiceProvider BuildScopedProvider(IUpdateService updater, IUpdateProgressSink sink, IToastNotificationService toast)
    {
        ServiceCollection sc = new();
        sc.AddSingleton(_services.GetRequiredService<IAppLogger>());
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
        private readonly List<int> _reports = [];
        private readonly List<string> _statuses = [];

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public IReadOnlyList<int> Reports
        {
            get { lock (_gate) { return [.. _reports]; } }
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

        public void Report(int percent)
        {
            lock (_gate) { _reports.Add(percent); }
            _reported.Set();
        }

        public void Close()
        {
            lock (_gate) { CloseCount++; }
        }

        public bool WaitForAnyReport(TimeSpan timeout) => _reported.Wait(timeout);
    }
}
