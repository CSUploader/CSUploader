// <copyright file="RapidgatorClientProxyTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Verifies that <see cref="RapidgatorClient"/> participates in the proxy rotation.
/// HttpHandler is built lazily — each <c>PrepareHttpHandler</c> call (which is what the
/// public CheckAccountAsync / UploadAsync entry points invoke internally) advances to
/// the next proxy from <see cref="ProxyManager.Current"/>.
/// </summary>
[Collection(nameof(AppSettingsCollection))]
public class RapidgatorClientProxyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly ProxySettingRepository _repo;
    private readonly ProxyManager _manager;
    private readonly ProxyManager? _previousCurrent;

    public RapidgatorClientProxyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _repo = new ProxySettingRepository(_factory);
        _manager = new ProxyManager(_repo, Mock.Of<IAppLogger>());

        _previousCurrent = ProxyManager.Current;
        ProxyManager.Current = _manager;
    }

    public void Dispose()
    {
        ProxyManager.Current = _previousCurrent;
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Construction_DoesNotPickProxyUntilRefreshOrUpload()
    {
        // Lazy-build semantics: HttpHandler isn't built until the client actually starts
        // an upload (or a manual PrepareHttpHandler). This is what lets a queued file
        // pick up "Use proxies for uploads" toggling off without any explicit refresh.
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        await _repo.InsertAsync(a);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());

        Assert.Equal(0, client.ActiveProxyId);
    }

    [Fact]
    public async Task PrepareHttpHandler_AfterConstruction_PicksFirstProxyFromRotation()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        await _repo.InsertAsync(a);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.PrepareHttpHandler();

        Assert.Equal(a.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task ThreeSequentialRefreshes_RoundRobinAcrossProxies()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client1 = new(Protocol.Http, Mock.Of<IAppLogger>());
        RapidgatorClient client2 = new(Protocol.Http, Mock.Of<IAppLogger>());
        RapidgatorClient client3 = new(Protocol.Http, Mock.Of<IAppLogger>());
        client1.PrepareHttpHandler();
        client2.PrepareHttpHandler();
        client3.PrepareHttpHandler();

        Assert.Equal(a.Id, client1.ActiveProxyId);
        Assert.Equal(b.Id, client2.ActiveProxyId);
        // Wraps back to A after exhausting the list.
        Assert.Equal(a.Id, client3.ActiveProxyId);
    }

    [Fact]
    public async Task PrepareHttpHandler_AfterProxiesEnabledTurnsOff_DropsActiveProxy()
    {
        // Regression: queued files should pick up the master "Use proxies for uploads"
        // toggle flipping off. With the lazy-build refactor this is automatic — the
        // first PrepareHttpHandler (or the next upload attempt) builds against the
        // current ProxyManager state.
        AppSettings previous = AppSettings.Current;
        AppSettings.Current = new AppSettings { ProxiesEnabled = true };
        try
        {
            ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
            await _repo.InsertAsync(a);
            await _manager.ReloadAsync();

            RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
            client.PrepareHttpHandler();
            Assert.Equal(a.Id, client.ActiveProxyId);

            // User flips the master switch off and saves.
            AppSettings.Current.ProxiesEnabled = false;
            client.PrepareHttpHandler();

            Assert.Equal(0, client.ActiveProxyId);
        }
        finally
        {
            AppSettings.Current = previous;
        }
    }

    [Fact]
    public async Task PrepareHttpHandler_AdvancesToNextProxy()
    {
        // Failed-upload retry path: each PrepareHttpHandler picks the next proxy in
        // rotation, so a bad proxy doesn't poison every retry.
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.PrepareHttpHandler();
        Assert.Equal(a.Id, client.ActiveProxyId);

        client.PrepareHttpHandler();

        Assert.Equal(b.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task PrepareHttpHandler_WrapsAroundAfterAllProxiesUsed()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.PrepareHttpHandler();   // -> a
        client.PrepareHttpHandler();   // -> b
        client.PrepareHttpHandler();   // wraps -> a

        Assert.Equal(a.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task PrepareHttpHandler_WhenNoProxiesConfigured_LeavesActiveProxyIdZero()
    {
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.PrepareHttpHandler();

        Assert.Equal(0, client.ActiveProxyId);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
