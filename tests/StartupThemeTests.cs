// <copyright file="StartupThemeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests;

/// <summary>
/// The early theme read that lets the head paint its first window in the saved theme. What these pin
/// is mostly its failure behaviour: it runs before anything is on screen, so "couldn't read it" has to
/// mean "start on the default", never "don't start".
/// </summary>
public class StartupThemeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;

    public StartupThemeTests()
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

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]      // the persister writes lowercase, but a hand-edited DB may not
    [InlineData("false", false)]
    public void ReadPersistedDarkMode_ReturnsTheSavedPreference(string stored, bool expected)
    {
        Save(stored);

        Assert.Equal(expected, StartupTheme.ReadPersistedDarkMode(_factory));
    }

    [Fact]
    public void ReadPersistedDarkMode_WithNothingSaved_IsNull_SoTheDefaultThemeStands()
    {
        // Null is "no opinion", NOT "light": the caller leaves App.axaml's default in place rather
        // than applying a theme nobody chose.
        Assert.Null(StartupTheme.ReadPersistedDarkMode(_factory));
    }

    [Fact]
    public void ReadPersistedDarkMode_IgnoresOtherSettings()
    {
        using (CSUploaderDbContext db = _factory.CreateDbContext())
        {
            db.Settings.Add(new SettingDbm { Key = SettingKey.CloseAction, Value = "true" });
            db.SaveChanges();
        }

        Assert.Null(StartupTheme.ReadPersistedDarkMode(_factory));
    }

    [Fact]
    public void ReadPersistedDarkMode_WhenTheStoreCannotBeRead_IsNull_RatherThanThrowing()
    {
        // First run (no database yet), an un-migrated schema, or a locked file. This runs on the UI
        // thread before the first window is built, so throwing here would mean the app doesn't start
        // — over a colour preference.
        Assert.Null(StartupTheme.ReadPersistedDarkMode(new ThrowingDbContextFactory()));
    }

    [Fact]
    public void ReadPersistedDarkMode_WithADatabaseThatHasNoTables_IsNull()
    {
        // The real first-run shape: a file exists but EnsureCreated hasn't run, so the query fails
        // on a missing table rather than on a missing file.
        using SqliteConnection empty = new("Data Source=:memory:");
        empty.Open();

        TestDbContextFactory factory = new(new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(empty)
            .Options);

        Assert.Null(StartupTheme.ReadPersistedDarkMode(factory));
    }

    [Fact]
    public void ReadPersistedDarkMode_DoesNotCreateOrChangeAnything()
    {
        // It is a read on the startup path: it must not write, and must not leave the database
        // populated in a way a later first-run check would misread.
        Assert.Null(StartupTheme.ReadPersistedDarkMode(_factory));

        using CSUploaderDbContext db = _factory.CreateDbContext();
        Assert.Empty(db.Settings);
    }

    private void Save(string value)
    {
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Settings.Add(new SettingDbm { Key = SettingKey.IsDarkMode, Value = value });
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => throw new InvalidOperationException("no database here");
    }
}
