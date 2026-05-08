// <copyright file="MainViewModelUpdateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.ViewModels;

public class MainViewModelUpdateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public MainViewModelUpdateTests()
    {
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
        sc.AddSingleton<UploadScheduler>();
        sc.AddSingleton<PackageManager>();
        sc.AddSingleton<CSUploader.Lib.Net.ProxyManager>();
        sc.AddSingleton(Mock.Of<IDialogService>());
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
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenNoUpdate_LeavesIsUpdateAvailableFalse()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync((UpdateAvailableInfo?)null);
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
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(info);
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
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync((UpdateAvailableInfo?)null);
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallUpdateCommand_WhenUpdateAvailable_CanExecute()
    {
        UpdateAvailableInfo info = new("9.9.9", new object());
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(info);
        MainViewModel vm = CreateVm(updater.Object);

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
    }

    private MainViewModel CreateVm(IUpdateService updater)
    {
        // Re-register the update service for this test. The service provider was built
        // without one, so we wrap it in a small composite that overrides that single key.
        ServiceProvider scoped = BuildScopedProvider(updater);
        return new MainViewModel(scoped);
    }

    private ServiceProvider BuildScopedProvider(IUpdateService updater)
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
        sc.AddSingleton(_services.GetRequiredService<CSUploader.Lib.Net.ProxyManager>());
        sc.AddSingleton(_services.GetRequiredService<IDialogService>());
        sc.AddSingleton(_services.GetRequiredService<UploadsViewModel>());
        sc.AddSingleton(_services.GetRequiredService<UploadedViewModel>());
        sc.AddSingleton(_services.GetRequiredService<SettingsViewModel>());
        sc.AddSingleton(_services.GetRequiredService<ConnectionManagerViewModel>());
        sc.AddSingleton(_services.GetRequiredService<LogsViewModel>());
        sc.AddSingleton(updater);
        return sc.BuildServiceProvider();
    }
}
