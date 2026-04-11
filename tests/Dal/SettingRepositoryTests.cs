// <copyright file="SettingRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class SettingRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;

    public SettingRepositoryTests()
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
    public async Task GetAllAsync_WhenNoSettings_ReturnsEmptyArray()
    {
        // Arrange
        var repo = new SettingRepository(_factory);

        // Act
        SettingDto[] result = await repo.GetAllAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task InsertAsync_InsertsSettingAndReturnsWithGeneratedId()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        var dto = new SettingDto { Key = "Theme", Value = "Dark" };

        // Act
        int rowsAffected = await repo.InsertAsync(dto);

        // Assert
        Assert.Equal(1, rowsAffected);
        Assert.NotEqual(0, dto.Id);

        SettingDto[] all = await repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Theme", all[0].Key);
        Assert.Equal("Dark", all[0].Value);
    }

    [Fact]
    public async Task FindByKeyAsync_FindsByKey_CaseInsensitive()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        await repo.InsertAsync(new SettingDto { Key = "OutputPath", Value = "/tmp/output" });

        // Act
        SettingDto? resultLower = await repo.FindByKeyAsync("outputpath");
        SettingDto? resultUpper = await repo.FindByKeyAsync("OUTPUTPATH");
        SettingDto? resultMixed = await repo.FindByKeyAsync("OutputPath");

        // Assert
        Assert.NotNull(resultLower);
        Assert.NotNull(resultUpper);
        Assert.NotNull(resultMixed);
        Assert.Equal("/tmp/output", resultLower.Value);
        Assert.Equal("/tmp/output", resultUpper.Value);
        Assert.Equal("/tmp/output", resultMixed.Value);
    }

    [Fact]
    public async Task FindByKeyAsync_WhenKeyDoesNotExist_ReturnsNull()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        await repo.InsertAsync(new SettingDto { Key = "Existing", Value = "yes" });

        // Act
        SettingDto? result = await repo.FindByKeyAsync("NonExistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesExistingSetting()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        var dto = new SettingDto { Key = "MaxRetries", Value = "3" };
        await repo.InsertAsync(dto);
        int insertedId = dto.Id;

        // Act
        dto.Value = "5";
        int rowsAffected = await repo.UpdateAsync(dto);

        // Assert
        Assert.Equal(1, rowsAffected);

        SettingDto? updated = await repo.FindByKeyAsync("MaxRetries");
        Assert.NotNull(updated);
        Assert.Equal(insertedId, updated.Id);
        Assert.Equal("5", updated.Value);
    }

    [Fact]
    public async Task DeleteAsync_WithDto_RemovesSetting()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        var dto = new SettingDto { Key = "ToDelete", Value = "bye" };
        await repo.InsertAsync(dto);

        // Act
        int rowsAffected = await repo.DeleteAsync(dto);

        // Assert
        Assert.Equal(1, rowsAffected);
        SettingDto[] all = await repo.GetAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task DeleteAsync_ById_RemovesCorrectSetting()
    {
        // Arrange
        var repo = new SettingRepository(_factory);
        var dto1 = new SettingDto { Key = "Keep", Value = "yes" };
        var dto2 = new SettingDto { Key = "Remove", Value = "no" };
        await repo.InsertAsync(dto1);
        await repo.InsertAsync(dto2);

        // Act
        int rowsAffected = await repo.DeleteAsync(dto2.Id);

        // Assert
        Assert.Equal(1, rowsAffected);

        SettingDto[] remaining = await repo.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("Keep", remaining[0].Key);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
