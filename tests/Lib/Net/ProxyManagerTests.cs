// <copyright file="ProxyManagerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Lib.Net;

public class ProxyManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly ProxySettingRepository _repo;
    private readonly ProxyManager _manager;

    public ProxyManagerTests()
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
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuildWebProxy_HttpType_ProducesHttpUri()
    {
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080 };

        IWebProxy? proxy = ProxyManager.BuildWebProxy(dto);

        WebProxy webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Equal("http://1.2.3.4:8080/", webProxy.Address!.ToString());
    }

    [Fact]
    public void BuildWebProxy_Socks5Type_ProducesSocks5Uri()
    {
        ProxySettingDto dto = new() { Type = ProxyType.Socks5, Host = "10.0.0.1", Port = 1080 };

        IWebProxy? proxy = ProxyManager.BuildWebProxy(dto);

        WebProxy webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Equal("socks5", webProxy.Address!.Scheme);
        Assert.Equal("10.0.0.1", webProxy.Address.Host);
        Assert.Equal(1080, webProxy.Address.Port);
    }

    [Fact]
    public void BuildWebProxy_WithCredentials_AppliesNetworkCredential()
    {
        ProxySettingDto dto = new()
        {
            Type = ProxyType.Http,
            Host = "1.2.3.4",
            Port = 8080,
            Username = "alice",
            Password = "s3cret",
        };

        IWebProxy? proxy = ProxyManager.BuildWebProxy(dto);

        Assert.NotNull(proxy);
        NetworkCredential creds = Assert.IsType<NetworkCredential>(proxy!.Credentials);
        Assert.Equal("alice", creds.UserName);
        Assert.Equal("s3cret", creds.Password);
    }

    [Fact]
    public void BuildWebProxy_NoneType_ReturnsNull()
    {
        ProxySettingDto dto = new() { Type = ProxyType.None, Host = "x", Port = 80 };
        Assert.Null(ProxyManager.BuildWebProxy(dto));
    }

    [Fact]
    public void BuildWebProxy_EmptyHost_ReturnsNull()
    {
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = string.Empty, Port = 80 };
        Assert.Null(ProxyManager.BuildWebProxy(dto));
    }

    [Fact]
    public async Task NextProxy_RotatesThroughEnabledProxiesInPriorityOrder()
    {
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true, Priority = 0 });
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true, Priority = 1 });
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "c", Port = 3, Enabled = true, Priority = 2 });
        await _manager.ReloadAsync();

        Assert.Equal("a", _manager.NextProxy()!.Host);
        Assert.Equal("b", _manager.NextProxy()!.Host);
        Assert.Equal("c", _manager.NextProxy()!.Host);
        // Wraps around
        Assert.Equal("a", _manager.NextProxy()!.Host);
    }

    [Fact]
    public async Task NextProxy_SkipsDisabledProxies()
    {
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "skip", Port = 1, Enabled = false, Priority = 0 });
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "keep", Port = 2, Enabled = true, Priority = 1 });
        await _manager.ReloadAsync();

        Assert.Equal("keep", _manager.NextProxy()!.Host);
    }

    [Fact]
    public async Task NextProxy_ReturnsNullWhenNoProxiesConfigured()
    {
        await _manager.ReloadAsync();
        Assert.Null(_manager.NextProxy());
    }

    [Fact]
    public async Task NextProxy_NoneTypeProxy_ReturnsNullEvenWhenScheduled()
    {
        // A "No Proxy" entry in the rotation acts as a sentinel for direct-connection;
        // NextProxy() should hand back null so callers fall through to direct.
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.None, Host = "ignored", Port = 0, Enabled = true, Priority = 0 });
        await _manager.ReloadAsync();

        Assert.Null(_manager.NextProxy());
    }

    [Fact]
    public async Task IncrementProblemsAsync_BumpsProblemsCountInDb()
    {
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true };
        await _repo.InsertAsync(dto);

        await _repo.IncrementProblemsAsync(dto.Id);
        await _repo.IncrementProblemsAsync(dto.Id);

        ProxySettingDto[] all = await _repo.GetAllAsync();
        Assert.Equal(2, all.Single().ProblemsCount);
    }

    [Fact]
    public async Task IncrementProblemsAsync_OnlyBumpsTheTargetedProxy()
    {
        ProxySettingDto a = new() { Type = ProxyType.Http, Host = "a", Port = 1, Enabled = true };
        ProxySettingDto b = new() { Type = ProxyType.Http, Host = "b", Port = 2, Enabled = true };
        await _repo.InsertAsync(a);
        await _repo.InsertAsync(b);

        await _repo.IncrementProblemsAsync(a.Id);

        ProxySettingDto[] all = await _repo.GetAllAsync();
        Assert.Equal(1, all.First(p => p.Host == "a").ProblemsCount);
        Assert.Equal(0, all.First(p => p.Host == "b").ProblemsCount);
    }

    [Fact]
    public async Task RecordFailure_EventuallyIncrementsProblemsCount()
    {
        // RecordFailure is fire-and-forget; poll until the increment lands so the test
        // doesn't depend on a fixed delay.
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true };
        await _repo.InsertAsync(dto);

        _manager.RecordFailure(dto.Id);

        for (int i = 0; i < 50; i++)
        {
            ProxySettingDto[] all = await _repo.GetAllAsync();
            if (all.Single().ProblemsCount == 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("ProblemsCount did not increment within 2.5s");
    }

    [Fact]
    public async Task ReloadAsync_PicksUpNewProxiesAddedAfterFirstLoad()
    {
        await _manager.ReloadAsync();
        Assert.Null(_manager.NextProxy());

        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "fresh", Port = 1, Enabled = true });
        await _manager.ReloadAsync();

        Assert.Equal("fresh", _manager.NextProxy()!.Host);
    }
}

internal class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
    : IDbContextFactory<CSUploaderDbContext>
{
    public CSUploaderDbContext CreateDbContext() => new(options);
}
