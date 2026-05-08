// <copyright file="ConnectionManagerViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
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

    private readonly CultureInfo _originalCulture;

    public ConnectionManagerViewModelTests()
    {
        // Pin Localizer to English so tests asserting specific status prefixes ("Failed", "OK")
        // don't break if a previous LocalizerTests run left a non-English culture on the singleton.
        _originalCulture = Localizer.Instance.Culture;
        Localizer.Instance.Culture = new CultureInfo("en");

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
        _manager = new ProxyManager(_repo, Mock.Of<IAppLogger>(), new AppSettings { ProxiesEnabled = true });
    }

    public void Dispose()
    {
        _connection.Dispose();
        Localizer.Instance.Culture = _originalCulture;
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

    [Theory]
    [InlineData("http://1.2.3.4", ProxyType.Http, 80)]
    [InlineData("https://example.com", ProxyType.Https, 443)]
    [InlineData("socks4://1.2.3.4", ProxyType.Socks4, 1080)]
    [InlineData("socks5://1.2.3.4", ProxyType.Socks5, 1080)]
    public void TryParseProxyLine_PortOmitted_UsesSchemeDefault(string line, ProxyType expectedType, int expectedPort)
    {
        Assert.True(ConnectionManagerViewModel.TryParseProxyLine(line, out ProxySettingDto dto));
        Assert.Equal(expectedType, dto.Type);
        Assert.Equal(expectedPort, dto.Port);
    }

    [Fact]
    public void TryParseProxyLine_RejectsUnknownScheme()
    {
        Assert.False(ConnectionManagerViewModel.TryParseProxyLine("ftp://1.2.3.4:21", out _));
        Assert.False(ConnectionManagerViewModel.TryParseProxyLine("not a url", out _));
    }

    [Fact]
    public void RemoveFailedCommand_RemovesOnlyRowsWithFailedTestOutcome()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(
                ConfirmationKeys.RemoveProxy, It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        ConnectionManagerViewModel vm = new(_repo, _manager, dialog.Object, Mock.Of<IAppLogger>());
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);

        // Manually paint test outcomes so we don't need a real network round-trip.
        vm.Proxies[0].TestStatus = "OK 100ms";
        vm.Proxies[0].TestOutcome = ProxyTestOutcome.Ok;
        vm.Proxies[1].TestStatus = "Failed: timeout";
        vm.Proxies[1].TestOutcome = ProxyTestOutcome.Failed;
        vm.Proxies[2].TestStatus = string.Empty; // untested
        vm.Proxies[1].Host = "bad-proxy";

        vm.RemoveFailedCommand.Execute(null);

        Assert.Equal(2, vm.Proxies.Count);
        Assert.DoesNotContain(vm.Proxies, p => p.Host == "bad-proxy");
    }

    [Fact]
    public void RemoveFailedCommand_WhenUserDeclines_KeepsAllRows()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(
                ConfirmationKeys.RemoveProxy, It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        ConnectionManagerViewModel vm = new(_repo, _manager, dialog.Object, Mock.Of<IAppLogger>());
        vm.AddCommand.Execute(null);
        vm.Proxies[0].TestStatus = "Failed: dead";
        vm.Proxies[0].TestOutcome = ProxyTestOutcome.Failed;

        vm.RemoveFailedCommand.Execute(null);

        Assert.Single(vm.Proxies);
    }

    [Fact]
    public void RemoveFailedCommand_NoFailedRows_DoesNotPromptOrRemove()
    {
        Mock<IDialogService> dialog = new();
        ConnectionManagerViewModel vm = new(_repo, _manager, dialog.Object, Mock.Of<IAppLogger>());
        vm.AddCommand.Execute(null);
        vm.Proxies[0].TestStatus = "OK 100ms";
        vm.Proxies[0].TestOutcome = ProxyTestOutcome.Ok;

        vm.RemoveFailedCommand.Execute(null);

        Assert.Single(vm.Proxies);
        dialog.Verify(d => d.ShowOptOutConfirmation(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TestCommand_DeadProxy_AutoDisablesProxy()
    {
        // A failed test should uncheck the row so subsequent uploads don't pick the
        // bad proxy. User still has to Save to persist.
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        ProxySettingItem item = vm.Proxies[0];
        item.Type = ProxyType.Http;
        item.Host = "127.0.0.1";
        item.Port = 1; // closed
        item.Enabled = true;
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.TestCommand.ExecuteAsync(item);

        Assert.False(item.Enabled);
        Assert.Equal(ProxyTestOutcome.Failed, item.TestOutcome);
    }

    [Fact]
    public async Task TestCommand_AutoDisableOff_KeepsEnabledOnFailure()
    {
        // The setting controls only the auto-uncheck behaviour — status icon must still
        // flip red so the user knows the proxy failed.
        ConnectionManagerViewModel vm = CreateVm();
        vm.AutoDisableFailingProxies = false;
        vm.AddCommand.Execute(null);
        ProxySettingItem item = vm.Proxies[0];
        item.Type = ProxyType.Http;
        item.Host = "127.0.0.1";
        item.Port = 1;
        item.Enabled = true;
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.TestCommand.ExecuteAsync(item);

        Assert.True(item.Enabled);
        Assert.Equal(ProxyTestOutcome.Failed, item.TestOutcome);
    }

    [Fact]
    public async Task ProxyResultObserved_Failure_AutoDisableOn_FlipsRowAndEnabled()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AutoDisableFailingProxies = true;
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true };
        await _repo.InsertAsync(dto);
        await vm.LoadAsync();

        _manager.ReportResult(dto.Id, success: false, message: "connection refused");

        ProxySettingItem item = vm.Proxies.Single();
        Assert.Equal(ProxyTestOutcome.Failed, item.TestOutcome);
        Assert.False(item.Enabled);
    }

    [Fact]
    public async Task ProxyResultObserved_Failure_AutoDisableOff_KeepsEnabled()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AutoDisableFailingProxies = false;
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true };
        await _repo.InsertAsync(dto);
        await vm.LoadAsync();

        _manager.ReportResult(dto.Id, success: false, message: "timeout");

        ProxySettingItem item = vm.Proxies.Single();
        Assert.Equal(ProxyTestOutcome.Failed, item.TestOutcome);
        Assert.True(item.Enabled);
    }

    [Fact]
    public async Task ProxyResultObserved_Success_FlipsRowToOk()
    {
        ConnectionManagerViewModel vm = CreateVm();
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "1.2.3.4", Port = 8080, Enabled = true };
        await _repo.InsertAsync(dto);
        await vm.LoadAsync();

        _manager.ReportResult(dto.Id, success: true);

        Assert.Equal(ProxyTestOutcome.Ok, vm.Proxies.Single().TestOutcome);
    }

    [Fact]
    public async Task SaveCommand_PersistsProxiesEnabledFlagToDatabase()
    {
        AppSettings settings = new();
        ConnectionManagerViewModel vm = new(
            _repo, _manager, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(),
            new SettingRepository(_factory), settings);

        await vm.LoadAsync();
        vm.ProxiesEnabled = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.ProxiesEnabled);
        SettingRepository repo = new(_factory);
        SettingDto? row = await repo.FindByKeyAsync(SettingKey.ProxiesEnabled);
        Assert.NotNull(row);
        Assert.Equal("false", row!.Value);
    }

    [Fact]
    public async Task SaveCommand_PersistsAutoDisableFlagToDatabase()
    {
        AppSettings settings = new();
        ConnectionManagerViewModel vm = new(
            _repo, _manager, Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(),
            new SettingRepository(_factory), settings);

        await vm.LoadAsync();
        vm.AutoDisableFailingProxies = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.AutoDisableFailingProxies);
        SettingRepository repo = new(_factory);
        SettingDto? row = await repo.FindByKeyAsync(SettingKey.AutoDisableFailingProxies);
        Assert.NotNull(row);
        Assert.Equal("false", row!.Value);
    }

    [Fact]
    public void TestOutcome_FreshItem_IsUntested()
    {
        ProxySettingItem item = new(new ProxySettingDto());

        Assert.Equal(ProxyTestOutcome.Untested, item.TestOutcome);
    }

    [Fact]
    public void TestOutcome_DefaultsToUntested()
    {
        // Fresh row: no status set, no outcome set → Untested (suppresses any icon until
        // a real test runs).
        ProxySettingItem item = new(new ProxySettingDto());

        Assert.Equal(ProxyTestOutcome.Untested, item.TestOutcome);
        Assert.Equal(string.Empty, item.TestStatus);
    }

    [Fact]
    public void TestOutcome_SetExplicitlyAlongsideStatus()
    {
        // TestOutcome is now an explicit field rather than derived from TestStatus —
        // this keeps the icon column working when status strings are localised.
        ProxySettingItem item = new(new ProxySettingDto())
        {
            TestStatus = "OK 250ms (1.2.3.4)",
            TestOutcome = ProxyTestOutcome.Ok,
        };

        Assert.Equal(ProxyTestOutcome.Ok, item.TestOutcome);
    }

    [Fact]
    public async Task TestCommand_DeadProxy_CapturesTransactionForDetailsModal()
    {
        // The Status cell shows a one-line summary; the full HTTP transaction is
        // captured on the row so the Details button can open the same request/response
        // viewer the Logs tab uses.
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        ProxySettingItem item = vm.Proxies[0];
        item.Type = ProxyType.Http;
        item.Host = "127.0.0.1";
        item.Port = 1; // closed
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.TestCommand.ExecuteAsync(item);

        Assert.NotNull(item.TestTransaction);
        Assert.True(item.HasTestDetails);
        Assert.Equal("http://127.0.0.1:1", item.TestTransaction!.Proxy);
    }

    [Fact]
    public async Task TestCommand_UpdatesStatusToFailedForObviouslyDeadProxy()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        ProxySettingItem item = vm.Proxies[0];
        item.Type = ProxyType.Http;
        item.Host = "127.0.0.1";
        item.Port = 1; // closed
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.TestCommand.ExecuteAsync(item);

        Assert.False(item.IsTesting);
        Assert.StartsWith("Failed", item.TestStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestCommand_NullParameter_DoesNothing()
    {
        ConnectionManagerViewModel vm = CreateVm();

        await vm.TestCommand.ExecuteAsync(null);

        // No items, no exceptions, nothing to assert beyond "didn't throw"
        Assert.Empty(vm.Proxies);
    }

    [Fact]
    public async Task TestCommand_WithSelectedItemsList_TestsEverySelectedRow()
    {
        // Right-click → Test on a multi-row selection passes DataGrid.SelectedItems (an
        // IList of ProxySettingItem). The command should fan out and exercise every row,
        // not just the first.
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        foreach (ProxySettingItem item in vm.Proxies)
        {
            item.Type = ProxyType.Http;
            item.Host = "127.0.0.1";
            item.Port = 1; // closed -> guaranteed failure for all
        }

        // Pass two of three rows; the third should remain untested.
        List<ProxySettingItem> selected = [vm.Proxies[0], vm.Proxies[2]];

        await vm.TestCommand.ExecuteAsync(selected);

        Assert.StartsWith("Failed", vm.Proxies[0].TestStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.Proxies[1].TestStatus);
        Assert.StartsWith("Failed", vm.Proxies[2].TestStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAllCommand_RunsAgainstEveryRow()
    {
        ConnectionManagerViewModel vm = CreateVm();
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        foreach (ProxySettingItem item in vm.Proxies)
        {
            item.Type = ProxyType.Http;
            item.Host = "127.0.0.1";
            item.Port = 1; // closed -> guaranteed failure for both
        }

        await vm.TestAllCommand.ExecuteAsync(null);

        Assert.All(vm.Proxies, p => Assert.StartsWith("Failed", p.TestStatus, StringComparison.Ordinal));
        Assert.All(vm.Proxies, p => Assert.False(p.IsTesting));
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
