// <copyright file="FileHosterLoginRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class FileHosterLoginRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;

    public FileHosterLoginRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);

        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllLogins()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user1",
            Password = "pass1",
            AccountType = AccountType.Free,
        });
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "user2",
            Password = "pass2",
            AccountType = AccountType.Premium,
        });

        // Act
        FileHosterLoginDto[] result = await repo.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Contains(result, r => r.FileHosterName == "Mega");
        Assert.Contains(result, r => r.FileHosterName == "Rapidgator");
    }

    [Fact]
    public async Task Roundtrip_PreservesLastRefreshedDateTime()
    {
        // Cheap insurance against forgetting one of the three mapper overrides
        // (MapToDto x2 + MapToDbm) when extending FileHosterLoginDto with new fields.
        // SQLite stores DateTime as ISO-8601 TEXT — millisecond precision survives;
        // ticks below the millisecond can be lost, so compare with TimeSpan tolerance.
        FileHosterLoginRepository repo = new(_factory);
        DateTime stamp = new(2026, 6, 9, 14, 23, 47, DateTimeKind.Local);
        FileHosterLoginDto inserted = new()
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
            LastRefreshedDateTime = stamp,
        };
        await repo.InsertAsync(inserted);

        FileHosterLoginDto? reloaded = await repo.FindAsync(inserted.Id);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.LastRefreshedDateTime);
        Assert.True(
            (reloaded.LastRefreshedDateTime!.Value - stamp).Duration() < TimeSpan.FromSeconds(1),
            $"Expected ~{stamp:O} but got {reloaded.LastRefreshedDateTime:O}");
    }

    [Fact]
    public async Task InsertAsync_InsertsLoginWithGeneratedId()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        var dto = new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "testuser",
            Password = "testpass",
            Disabled = false,
            AccountType = AccountType.Premium,
        };

        // Act
        int rowsAffected = await repo.InsertAsync(dto);

        // Assert
        Assert.Equal(1, rowsAffected);
        Assert.NotEqual(0, dto.Id);

        FileHosterLoginDto[] all = await repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Mega", all[0].FileHosterName);
        Assert.Equal("testuser", all[0].Username);
        Assert.Equal("testpass", all[0].Password);
        Assert.False(all[0].Disabled);
        Assert.Equal(AccountType.Premium, all[0].AccountType);
    }

    [Fact]
    public async Task FindAsync_ByName_ReturnsMatchingLogins()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user1",
            Password = "pass1",
        });
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user2",
            Password = "pass2",
        });
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "user3",
            Password = "pass3",
        });

        // Act
        FileHosterLoginDto[] result = await repo.FindAsync("Mega");

        // Assert
        Assert.Equal(2, result.Length);
        Assert.All(result, r => Assert.Equal("Mega", r.FileHosterName));
    }

    [Fact]
    public async Task FindAsync_ByNameAndUsername_ReturnsSingleMatch()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user1",
            Password = "pass1",
        });
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user2",
            Password = "pass2",
        });

        // Act
        FileHosterLoginDto? result = await repo.FindAsync("Mega", "user2");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("user2", result.Username);
        Assert.Equal("pass2", result.Password);
    }

    [Fact]
    public async Task FindAsync_ByNameAndUsername_WhenNotFound_ReturnsNull()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        await repo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "user1",
            Password = "pass1",
        });

        // Act
        FileHosterLoginDto? result = await repo.FindAsync("Mega", "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ById_RemovesCorrectLogin()
    {
        // Arrange
        var repo = new FileHosterLoginRepository(_factory);
        var dto1 = new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "keep",
            Password = "pass1",
        };
        var dto2 = new FileHosterLoginDto
        {
            FileHosterName = "Mega",
            Username = "remove",
            Password = "pass2",
        };
        await repo.InsertAsync(dto1);
        await repo.InsertAsync(dto2);

        // Act
        int rowsAffected = await repo.DeleteAsync(dto2.Id);

        // Assert
        Assert.Equal(1, rowsAffected);

        FileHosterLoginDto[] remaining = await repo.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("keep", remaining[0].Username);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
