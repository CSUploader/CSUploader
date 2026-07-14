// <copyright file="MainViewModelDisposeTests.cs" company="CSUploader">
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
/// Phase 9 ledger fix (c): <see cref="MainViewModel"/> is <see cref="IDisposable"/>. Its ctor starts a 6h
/// update timer and subscribes to the process-global <see cref="Localizer.Instance"/> singleton (plus the
/// named logger handler), so every un-disposed instance leaks a subscription for the process lifetime.
/// Dispose stops the timer and detaches BOTH subscriptions; the same delegate instance the ctor added is
/// removed (the Localizer handler is captured in a field, not an un-detachable inline lambda).
/// </summary>
[Collection(LocalizerCollection.Name)]
public class MainViewModelDisposeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly InlineUiDispatcher _dispatcher;
    private readonly CultureInfo _originalCulture;

    public MainViewModelDisposeTests()
    {
        // MainViewModel's ctor subscribes to the process-global Localizer singleton; pin the culture and
        // serialize via LocalizerCollection so a peer test's culture flip can't perturb this one.
        _originalCulture = Localizer.Instance.Culture;
        Localizer.Instance.Culture = new CultureInfo("en");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // Registered as the concrete instance so the test can inspect the timer the VM ctor started.
        _dispatcher = new InlineUiDispatcher();

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
        sc.AddSingleton<IUiDispatcher>(_dispatcher);

        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync((UpdateAvailableInfo?)null);
        sc.AddSingleton(updater.Object);
        sc.AddSingleton(Mock.Of<IUpdateProgressSink>());

        sc.AddSingleton<UploadsViewModel>();
        sc.AddSingleton<UploadedViewModel>();
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
    public void Dispose_StopsUpdateTimer()
    {
        MainViewModel vm = new(_services);

        // MainViewModel creates its 6h update timer LAST in its ctor — after resolving the sub-VMs, one of
        // which (UploadsViewModel) registers its own 200ms refresh timer — so the update timer is the
        // most-recently-added on the shared dispatcher.
        InlineUiDispatcher.TestTimer updateTimer = _dispatcher.Timers[^1];
        Assert.True(updateTimer.IsRunning); // the ctor started it.

        vm.Dispose();

        Assert.False(updateTimer.IsRunning); // Dispose calls IUiTimer.Stop().
    }

    [Fact]
    public void Dispose_UnsubscribesLocalizer_NoMorePropertyChanged()
    {
        MainViewModel vm = new(_services);
        bool raisedAfterDispose = false;

        vm.Dispose();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowTitle))
            {
                raisedAfterDispose = true;
            }
        };

        // Reassigning Localizer.Culture raises PropertyChanged ONLY on a real change (Localizer.cs guards
        // Equals(field, value)); the ctor pinned "en", so flip to a different culture. This would fire the
        // VM's WindowTitle refresh via the ctor's Localizer subscription if Dispose had not detached it.
        Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");

        Assert.False(raisedAfterDispose);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        MainViewModel vm = new(_services);
        InlineUiDispatcher.TestTimer updateTimer = _dispatcher.Timers[^1]; // MainViewModel's 6h timer (last-added).

        vm.Dispose();
        vm.Dispose(); // second call must be a genuine no-op, not throw.

        Assert.False(updateTimer.IsRunning);
    }
}
