// <copyright file="PackageManagerRenameTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CSUploader.Tests.Upload;

/// <summary>
/// <see cref="PackageManager.RenamePackage"/> — the Uploads tab's inline package rename. The in-memory
/// name changes immediately (with a PropertyChanged raise, since the selective per-tick refresh won't
/// repaint an idle package), and the DB row follows fire-and-forget so the rename survives a restart
/// (and the History tab's load-time name join picks it up).
/// </summary>
public sealed class PackageManagerRenameTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;
    private readonly UploadPackageRepository _packageRepo;

    public PackageManagerRenameTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        TestDbContextFactory factory = new(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        AppSettings settings = new();
        DefaultFileHosterRegistry registry = new([]);
        _packageRepo = new UploadPackageRepository(factory);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _packageManager = new PackageManager(
            settings, _scheduler, _packageRepo, new UploadPackageFileRepository(factory),
            new FileHosterLoginRepository(factory), Mock.Of<IAppLogger>(), registry);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RenamePackage_UpdatesInMemoryWithNotification_AndPersistsTheDbRow()
    {
        UploadPackageDto dto = new() { Name = "Old name", CreatedDateTime = DateTime.Now, IsCompleted = false };
        await _packageRepo.InsertAsync(dto);

        Package package = MakePackage("Old name");
        package.DbId = dto.Id;

        List<string?> raised = [];
        package.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _packageManager.RenamePackage(package, "New name");

        // In-memory immediately, with the notification the grid cell repaints from.
        Assert.Equal("New name", package.Name);
        Assert.Contains(nameof(Package.Name), raised);

        // The DB row follows (fire-and-forget) — poll briefly for the write to land.
        UploadPackageDto? persisted = null;
        for (int i = 0; i < 200; i++)
        {
            persisted = await _packageRepo.FindAsync(dto.Id);
            if (persisted?.Name == "New name")
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.Equal("New name", persisted?.Name);
    }

    [Fact]
    public void RenamePackage_WithoutDbRow_RenamesInMemoryOnly_NoThrow()
    {
        Package package = MakePackage("Old name"); // DbId stays null (not yet persisted)

        _packageManager.RenamePackage(package, "New name");

        Assert.Equal("New name", package.Name);
    }

    private static Package MakePackage(string name)
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        return new Package(new PackageOptions
        {
            Title = name,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        });
    }

    private static AttemptRunner BuildAttemptRunner()
    {
        DefaultFileHosterRegistry registry = new([]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
