// <copyright file="ConnectionManagerViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

public class ConnectionManagerViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly ProxySettingRepository _repo;
    private readonly ProxyManager _manager;

    public ConnectionManagerViewModelTests()
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

    private ConnectionManagerViewModel CreateVm() =>
        new(_repo, _manager, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>());

    [Fact]
    public async Task LoadAsync_PopulatesProxiesInPriorityOrder()
    {
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "low", Port = 1, Priority = 5 });
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "high", Port = 2, Priority = 0 });

        ConnectionManagerViewModel vm = CreateVm();
        await vm.LoadAsync();

        Assert.Equal(2, vm.Proxies.Count);
        Assert.Equal("high", vm.Proxies[0].Host);
        Assert.Equal("low", vm.Proxies[1].Host);
    }

    [Fact]
    public async Task SaveAsync_InsertsNewProxiesAndAssignsIds()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        vm.Proxies[0].Host = "first";
        vm.Proxies[1].Host = "second";

        await vm.SaveCommand.ExecuteAsync(null);

        ProxySettingDto[] persisted = await _repo.GetAllAsync();
        Assert.Equal(2, persisted.Length);
        Assert.Contains(persisted, p => p.Host == "first");
        Assert.Contains(persisted, p => p.Host == "second");
        Assert.All(vm.Proxies, item => Assert.NotEqual(0, item.Dto.Id));
    }

    [Fact]
    public async Task SaveAsync_DeletesProxiesRemovedFromTheList()
    {
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "stays", Port = 1 });
        ProxySettingDto going = new() { Type = ProxyType.Http, Host = "going", Port = 2 };
        await _repo.InsertAsync(going);

        ConnectionManagerViewModel vm = CreateVm();
        await vm.LoadAsync();

        ProxySettingItem toRemove = vm.Proxies.Single(i => i.Host == "going");
        vm.Proxies.Remove(toRemove);

        await vm.SaveCommand.ExecuteAsync(null);

        ProxySettingDto[] persisted = await _repo.GetAllAsync();
        Assert.Single(persisted);
        Assert.Equal("stays", persisted[0].Host);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingProxiesInPlace()
    {
        ProxySettingDto original = new() { Type = ProxyType.Http, Host = "old.example", Port = 80 };
        await _repo.InsertAsync(original);

        ConnectionManagerViewModel vm = CreateVm();
        await vm.LoadAsync();
        vm.Proxies[0].Host = "new.example";
        vm.Proxies[0].Port = 8080;

        await vm.SaveCommand.ExecuteAsync(null);

        ProxySettingDto reloaded = (await _repo.GetAllAsync()).Single();
        Assert.Equal(original.Id, reloaded.Id);
        Assert.Equal("new.example", reloaded.Host);
        Assert.Equal(8080, reloaded.Port);
    }

    [Fact]
    public async Task SaveAsync_RenumbersPriorityFromCurrentOrder()
    {
        // Two existing rows with priorities 0 and 1 — we'll move the second above the
        // first via MoveUp and expect Save to renumber so the new top has Priority=0.
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "a", Port = 1, Priority = 0 });
        await _repo.InsertAsync(new ProxySettingDto { Type = ProxyType.Http, Host = "b", Port = 2, Priority = 1 });

        ConnectionManagerViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.MoveUpCommand.Execute(vm.Proxies[1]);    // b -> top
        await vm.SaveCommand.ExecuteAsync(null);

        ProxySettingDto[] persisted = [.. (await _repo.GetAllAsync()).OrderBy(p => p.Priority)];
        Assert.Equal("b", persisted[0].Host);
        Assert.Equal(0, persisted[0].Priority);
        Assert.Equal("a", persisted[1].Host);
        Assert.Equal(1, persisted[1].Priority);
    }

    [Fact]
    public async Task SaveAsync_ReloadsProxyManagerSoNewProxiesEnterRotation()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        vm.Proxies[0].Host = "fresh";
        vm.Proxies[0].Port = 8080;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("fresh", _manager.NextProxy()!.Host);
    }

    [Fact]
    public void TryParseProxyLine_ParsesAllSchemes()
    {
        Assert.True(ConnectionManagerViewModel.TryParseProxyLine("http://1.2.3.4:8080", out ProxySettingDto http));
        Assert.Equal(ProxyType.Http, http.Type);
        Assert.Equal("1.2.3.4", http.Host);
        Assert.Equal(8080, http.Port);

        Assert.True(ConnectionManagerViewModel.TryParseProxyLine("socks5://10.0.0.1:1080", out ProxySettingDto socks));
        Assert.Equal(ProxyType.Socks5, socks.Type);

        Assert.True(ConnectionManagerViewModel.TryParseProxyLine("https://example.com:443", out ProxySettingDto https));
        Assert.Equal(ProxyType.Https, https.Type);
        Assert.Equal("example.com", https.Host);
    }

    [Fact]
    public void TryParseProxyLine_ParsesCredentials()
    {
        Assert.True(ConnectionManagerViewModel.TryParseProxyLine("socks5://alice:s3cret@10.0.0.1:1080", out ProxySettingDto dto));

        Assert.Equal("alice", dto.Username);
        Assert.Equal("s3cret", dto.Password);
        Assert.Equal("10.0.0.1", dto.Host);
        Assert.Equal(1080, dto.Port);
    }

    [Fact]
    public void TryParseProxyLine_RejectsUnknownScheme()
    {
        Assert.False(ConnectionManagerViewModel.TryParseProxyLine("ftp://1.2.3.4:21", out _));
        Assert.False(ConnectionManagerViewModel.TryParseProxyLine("not a url", out _));
    }

    [Fact]
    public void FormatProxyLine_RoundTripsThroughTryParse()
    {
        ProxySettingItem item = new(new ProxySettingDto
        {
            Type = ProxyType.Socks5,
            Host = "10.0.0.1",
            Port = 1080,
            Username = "alice",
            Password = "s3cret",
        });

        string formatted = ConnectionManagerViewModel.FormatProxyLine(item);
        Assert.True(ConnectionManagerViewModel.TryParseProxyLine(formatted, out ProxySettingDto parsed));

        Assert.Equal(ProxyType.Socks5, parsed.Type);
        Assert.Equal("10.0.0.1", parsed.Host);
        Assert.Equal(1080, parsed.Port);
        Assert.Equal("alice", parsed.Username);
        Assert.Equal("s3cret", parsed.Password);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
