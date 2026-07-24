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
            Type = ProxyType.Http,
            Host = "1.2.3.4",
            Port = 8080,
            Enabled = true,
            Priority = 0,
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
            Type = ProxyType.None,
            Host = string.Empty,
            Port = 0,
            Enabled = true,
            Priority = 0,
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
            Type = ProxyType.Http,
            Host = "10.0.0.1",
            Port = 3128,
            Enabled = true,
            Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxyChoice? choice = ((IProxySource)manager).Next();

        Assert.NotNull(choice);
        Assert.NotSame(ProxyChoice.Direct, choice);
        Assert.Contains("10.0.0.1:3128", choice!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetById_ZeroReturnsDirect_ForPinnedToDirectSentinel()
    {
        // A pinned-proxy id of 0 is the sentinel for "this account was signed in over a
        // direct connection — keep using direct." GetById must surface ProxyChoice.Direct
        // for that case without touching the rotation list.
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);

        ProxyChoice? choice = ((IProxySource)manager).GetById(0);

        Assert.Same(ProxyChoice.Direct, choice);
    }

    [Fact]
    public async Task GetById_ExistingEnabledProxy_ReturnsResolvedChoice()
    {
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.Http,
            Host = "10.0.0.5",
            Port = 3128,
            Enabled = true,
            Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxySettingDto seeded = (await new ProxySettingRepository(_factory).GetAllAsync()).Single();
        ProxyChoice? choice = ((IProxySource)manager).GetById(seeded.Id);

        Assert.NotNull(choice);
        Assert.Equal(seeded.Id, choice!.Id);
        Assert.Contains("10.0.0.5:3128", choice.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetById_DisabledOrMissingProxy_ReturnsNullSoCallerCanFailFast()
    {
        // Pinned proxy was disabled in Connection Manager since the pin was set. AttemptRunner
        // relies on null here to refuse the upload rather than silently rotating off-pin
        // (which would invalidate the IP-bound session cookie).
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.Http,
            Host = "10.0.0.5",
            Port = 3128,
            Enabled = false,
            Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        // GetById on a missing-from-rotation id (the disabled proxy never makes it into _proxies).
        ProxyChoice? choice = ((IProxySource)manager).GetById(9999);

        Assert.Null(choice);
    }

    [Fact]
    public async Task GetById_UseProxiesOff_ReturnsDirectForPinnedProxy_GlobalToggleWins()
    {
        // The master "Use Proxies" switch wins over a per-account pin: with proxies globally off,
        // a stale PinnedProxyId must NOT resurrect a proxy (nor fall through to the OS system proxy).
        // Mirrors Next() honouring the toggle. Regression for the sign-in/upload-uses-a-proxy-when-off bug.
        await SeedProxyAsync(new ProxySettingDto
        {
            Type = ProxyType.Http,
            Host = "10.0.0.9",
            Port = 3128,
            Enabled = true,
            Priority = 0,
        });
        AppSettings settings = new() { ProxiesEnabled = false };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>(), settings);
        await manager.ReloadAsync();

        ProxySettingDto seeded = (await new ProxySettingRepository(_factory).GetAllAsync()).Single();
        ProxyChoice? choice = ((IProxySource)manager).GetById(seeded.Id);

        Assert.Same(ProxyChoice.Direct, choice);
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
