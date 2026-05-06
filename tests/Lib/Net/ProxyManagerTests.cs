// <copyright file="ProxyManagerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
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
    public async Task TestProxyAsync_NoneType_FailsWithoutNetworkCall()
    {
        ProxySettingDto dto = new() { Type = ProxyType.None, Host = "1.2.3.4", Port = 80 };

        ProxyTestResult result = await ProxyManager.TestProxyAsync(dto, Mock.Of<IAppLogger>());

        Assert.False(result.Success);
        Assert.Contains("None", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestProxyAsync_EmptyHost_Fails()
    {
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = string.Empty, Port = 80 };

        ProxyTestResult result = await ProxyManager.TestProxyAsync(dto, Mock.Of<IAppLogger>());

        Assert.False(result.Success);
        Assert.Contains("invalid", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestProxyAsync_DeadProxy_FailsWithinTimeout()
    {
        // 127.0.0.1:1 — port 1 is reserved and almost certainly closed locally,
        // so the connection refused error path is exercised quickly.
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "127.0.0.1", Port = 1 };

        ProxyTestResult result = await ProxyManager.TestProxyAsync(dto, Mock.Of<IAppLogger>(), TimeSpan.FromSeconds(3));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task TestProxyAsync_LogsTransactionViaSuppliedLogger()
    {
        // Failed-proxy case still routes through HttpHandler, so the IAppLogger should
        // see at least one LogType.Http entry — that's how proxy tests show up in the
        // Logs tab alongside upload traffic.
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "127.0.0.1", Port = 1 };
        Mock<IAppLogger> logger = new();

        await ProxyManager.TestProxyAsync(dto, logger.Object, TimeSpan.FromSeconds(3));

        logger.Verify(
            l => l.Log(
                It.IsAny<object?>(),
                LogType.Http,
                It.IsAny<string>(),
                It.IsAny<HttpTransaction?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public static void Ok_BodyIsRealIp_PopulatesDetectedIp()
    {
        ProxyTestResult result = ProxyTestResult.Ok(123, "1.2.3.4");

        Assert.True(result.Success);
        Assert.Equal("1.2.3.4", result.DetectedIp);
        Assert.Equal("1.2.3.4", result.Body);
    }

    [Fact]
    public static void Ok_BodyIsHtml_LeavesDetectedIpNullButKeepsFullBody()
    {
        // Squid-style proxies intercept the request and return a full HTML error page.
        // We treat the test as Success (the connection went through) but only set
        // DetectedIp when the body is a parseable IP; everything else is Body-only.
        const string html = "<html><head><title>ERROR</title></head><body>blocked</body></html>";

        ProxyTestResult result = ProxyTestResult.Ok(123, html);

        Assert.True(result.Success);
        Assert.Null(result.DetectedIp);
        Assert.Equal(html, result.Body);
    }

    [Fact]
    public static void Failed_StoresMessageInBothMessageAndBody()
    {
        ProxyTestResult result = ProxyTestResult.Failed("Connection refused.");

        Assert.False(result.Success);
        Assert.Equal("Connection refused.", result.Message);
        Assert.Equal("Connection refused.", result.Body);
    }

    [Fact]
    public async Task TestProxyAsync_BypassesMockServerRewriting()
    {
        // Regression: the dev "mock server" toggle was rewriting api.ipify.org to
        // localhost:8080/api, which made every proxy test go to the dev sandbox
        // instead of the real upstream. The connectivity test must hit the configured
        // TestEndpoint regardless of mock-server settings.
        AppSettings previous = AppSettings.Current;
        AppSettings.Current = new AppSettings
        {
            UseMockServer = true,
            MockServerBaseUrl = "http://localhost:8080",
        };
        try
        {
            ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "127.0.0.1", Port = 1 };
            HttpTransaction? captured = null;
            Mock<IAppLogger> logger = new();
            logger.Setup(l => l.Log(
                    It.IsAny<object?>(),
                    It.IsAny<LogType>(),
                    It.IsAny<string>(),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()))
                .Callback<object?, LogType, string, HttpTransaction?, string, string, int>(
                    (_, _, _, tx, _, _, _) => captured ??= tx);

            await ProxyManager.TestProxyAsync(dto, logger.Object, TimeSpan.FromSeconds(3));

            Assert.NotNull(captured);
            Assert.Equal(ProxyManager.TestEndpoint, captured!.Url);
        }
        finally
        {
            AppSettings.Current = previous;
        }
    }

    [Fact]
    public async Task TestProxyAsync_LoggedTransaction_CarriesProxyDescription()
    {
        // The whole point of plumbing the proxy into HttpTransaction: a glance at the
        // Logs tab should tell you which proxy a request went through. Verify the
        // captured transaction's Proxy field reflects the configured proxy.
        ProxySettingDto dto = new() { Type = ProxyType.Http, Host = "127.0.0.1", Port = 1 };
        HttpTransaction? captured = null;
        Mock<IAppLogger> logger = new();
        logger.Setup(l => l.Log(
                It.IsAny<object?>(),
                LogType.Http,
                It.IsAny<string>(),
                It.IsAny<HttpTransaction?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()))
            .Callback<object?, LogType, string, HttpTransaction?, string, string, int>(
                (_, _, _, tx, _, _, _) => captured ??= tx);

        await ProxyManager.TestProxyAsync(dto, logger.Object, TimeSpan.FromSeconds(3));

        Assert.NotNull(captured);
        Assert.Equal("http://127.0.0.1:1", captured!.Proxy);
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
