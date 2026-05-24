// <copyright file="ProxyManagerSourceTests.cs" company="CSUploader">
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

namespace CSUploader.Tests.Lib.Net;

public class ProxyManagerSourceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;

    public ProxyManagerSourceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>().UseSqlite(_conn).Options;
        _factory = new Factory(options);
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() { _conn.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public async Task Next_UseProxiesOnButNoEnabledProxiesExist_ReturnsNullSoCallerCanRefuse()
    {
        // The load-bearing case for the new behaviour: Use Proxies is ON, but the rotation
        // is empty. Returning ProxyChoice.Direct here would silently ship bytes over the
        // user's real IP — exactly what enabling proxies is meant to prevent.
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync(); // no proxies in DB → empty rotation

        ProxyChoice? choice = ((IProxySource)manager).Next();

        Assert.Null(choice);
    }

    [Fact]
    public async Task Next_UseProxiesOff_ReturnsDirectEvenWhenProxiesAreConfigured()
    {
        // Pre-existing behaviour worth pinning: the master toggle wins. Even if rows exist,
        // a disabled toggle means the user is opting into direct on purpose.
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true, Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = false };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxyChoice? choice = ((IProxySource)manager).Next();

        Assert.Same(ProxyChoice.Direct, choice);
    }

    [Fact]
    public async Task Next_UseProxiesOnAndRotationHitsExplicitNoneEntry_ReturnsDirect()
    {
        // A ProxyType.None entry is a user-added "include a direct slot in the rotation"
        // configuration. That's not the same as "couldn't get a proxy" — it's a deliberate
        // choice and must still translate to Direct, NOT to a null refusal.
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.None, Host = string.Empty, Port = 0, Enabled = true, Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxyChoice? choice = ((IProxySource)manager).Next();

        Assert.Same(ProxyChoice.Direct, choice);
    }

    [Fact]
    public async Task Next_UseProxiesOnWithUsableProxy_ReturnsResolvedProxyNotDirect()
    {
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.Http, Host = "10.0.0.1", Port = 3128, Enabled = true, Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxyChoice? choice = ((IProxySource)manager).Next();

        Assert.NotNull(choice);
        Assert.NotSame(ProxyChoice.Direct, choice);
        Assert.Contains("10.0.0.1:3128", choice!.Description, StringComparison.Ordinal);
    }

    private async Task SeedProxyAsync(ProxySettingDto dto)
    {
        // Go through the repo so the DTO → Dbm mapping stays the responsibility of one place.
        ProxySettingRepository repo = new(_factory);
        await repo.InsertAsync(dto);
    }

    private sealed class Factory(DbContextOptions<CSUploaderDbContext> options) : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
