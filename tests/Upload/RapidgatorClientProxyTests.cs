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
/// Verifies that <see cref="RapidgatorClient"/> participates in the proxy rotation:
/// new clients pick the next proxy from <see cref="ProxyManager.Current"/>, and a call
/// to <see cref="RapidgatorClient.RefreshConnection"/> advances to the proxy after that
/// — the path retried-after-failure uploads take.
/// </summary>
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
    public async Task Construction_WithNoProxies_LeavesActiveProxyIdZero()
    {
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());

        Assert.Equal(0, client.ActiveProxyId);
    }

    [Fact]
    public async Task Construction_PicksFirstProxyFromRotation()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        await _repo.InsertAsync(a);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());

        Assert.Equal(a.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task TwoSequentialClients_RoundRobinAcrossProxies()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client1 = new(Protocol.Http, Mock.Of<IAppLogger>());
        RapidgatorClient client2 = new(Protocol.Http, Mock.Of<IAppLogger>());
        RapidgatorClient client3 = new(Protocol.Http, Mock.Of<IAppLogger>());

        Assert.Equal(a.Id, client1.ActiveProxyId);
        Assert.Equal(b.Id, client2.ActiveProxyId);
        // Wraps back to A after exhausting the list.
        Assert.Equal(a.Id, client3.ActiveProxyId);
    }

    [Fact]
    public async Task RefreshConnection_AdvancesToNextProxy()
    {
        // The exact scenario from the user's report: a failed upload's retry should
        // pick a different proxy so a dead proxy doesn't poison every retry.
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        Assert.Equal(a.Id, client.ActiveProxyId);

        client.RefreshConnection();

        Assert.Equal(b.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task RefreshConnection_WrapsAroundAfterAllProxiesUsed()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.RefreshConnection();   // -> b
        client.RefreshConnection();   // wraps -> a

        Assert.Equal(a.Id, client.ActiveProxyId);
    }

    [Fact]
    public async Task RefreshConnection_WhenNoProxiesConfigured_LeavesActiveProxyIdZero()
    {
        await _manager.ReloadAsync();

        RapidgatorClient client = new(Protocol.Http, Mock.Of<IAppLogger>());
        client.RefreshConnection();

        Assert.Equal(0, client.ActiveProxyId);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
