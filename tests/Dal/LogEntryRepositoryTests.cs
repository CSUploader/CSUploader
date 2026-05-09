// <copyright file="LogEntryRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class LogEntryRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly LogEntryRepository _repo;

    public LogEntryRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);

        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _repo = new LogEntryRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InsertAsync_PersistsAllFieldsIncludingDateTime()
    {
        DateTime when = new(2025, 6, 1, 12, 34, 56, DateTimeKind.Local);
        LogEntryDto dto = new()
        {
            DateTime = when,
            LogType = LogType.Error,
            Filename = "Foo.cs",
            Function = "Bar",
            LineNumber = 42,
            ThreadId = 7,
            Message = "boom",
        };

        await _repo.InsertAsync(dto);

        LogEntryDto[] all = await _repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(when, all[0].DateTime);
        Assert.Equal(LogType.Error, all[0].LogType);
        Assert.Equal("Foo.cs", all[0].Filename);
        Assert.Equal("Bar", all[0].Function);
        Assert.Equal(42, all[0].LineNumber);
        Assert.Equal(7, all[0].ThreadId);
        Assert.Equal("boom", all[0].Message);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsLatestNInChronologicalOrder()
    {
        for (int i = 1; i <= 5; i++)
        {
            await _repo.InsertAsync(new LogEntryDto
            {
                DateTime = new DateTime(2025, 1, i, 0, 0, 0, DateTimeKind.Local),
                LogType = LogType.Status,
                Message = $"msg{i}",
            });
        }

        LogEntryDto[] recent = await _repo.GetRecentAsync(3);

        // Latest 3 ascending: msg3, msg4, msg5
        Assert.Equal(3, recent.Length);
        Assert.Equal("msg3", recent[0].Message);
        Assert.Equal("msg4", recent[1].Message);
        Assert.Equal("msg5", recent[2].Message);
    }

    [Fact]
    public async Task GetRecentAsync_WhenFewerEntriesExist_ReturnsAll()
    {
        await _repo.InsertAsync(new LogEntryDto
        {
            DateTime = DateTime.Now,
            LogType = LogType.Status,
            Message = "only",
        });

        LogEntryDto[] recent = await _repo.GetRecentAsync(100);

        Assert.Single(recent);
        Assert.Equal("only", recent[0].Message);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_RemovesOnlyEntriesBeforeCutoff()
    {
        DateTime cutoff = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Local);
        await _repo.InsertAsync(new LogEntryDto { DateTime = cutoff.AddDays(-2), LogType = LogType.Status, Message = "old" });
        await _repo.InsertAsync(new LogEntryDto { DateTime = cutoff.AddDays(-1), LogType = LogType.Status, Message = "older" });
        await _repo.InsertAsync(new LogEntryDto { DateTime = cutoff.AddDays(1), LogType = LogType.Status, Message = "kept" });

        int removed = await _repo.DeleteOlderThanAsync(cutoff);

        Assert.Equal(2, removed);
        LogEntryDto[] remaining = await _repo.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("kept", remaining[0].Message);
    }

    [Fact]
    public async Task LogTypeRoundTrip_PreservesEnumValue()
    {
        foreach (LogType type in Enum.GetValues<LogType>())
        {
            await _repo.InsertAsync(new LogEntryDto
            {
                DateTime = DateTime.Now,
                LogType = type,
                Message = type.ToString(),
            });
        }

        LogEntryDto[] all = await _repo.GetAllAsync();
        foreach (LogType type in Enum.GetValues<LogType>())
        {
            Assert.Contains(all, e => e.LogType == type && e.Message == type.ToString());
        }
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
