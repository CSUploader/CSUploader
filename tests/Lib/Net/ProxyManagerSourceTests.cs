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

[Collection(nameof(AppSettingsCollection))]
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
    public void Next_WithNoProxiesEnabled_ReturnsDirect()
    {
        AppSettings.Current = new AppSettings { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>());

        ProxyChoice choice = ((IProxySource)manager).Next();

        Assert.Same(ProxyChoice.Direct, choice);
    }

    private sealed class Factory(DbContextOptions<CSUploaderDbContext> options) : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
