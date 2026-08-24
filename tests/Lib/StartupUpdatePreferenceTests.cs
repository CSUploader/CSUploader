// <copyright file="StartupUpdatePreferenceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Lib;

/// <summary>
/// The preference read that happens before any window exists.
/// <para>
/// It cannot come from <c>AppSettings</c>, which holds only defaults until hydration runs long after
/// the first window is on screen — so a splash decision made from there would ignore what the user
/// chose. This reads the store directly, and must never be able to stop the app starting.
/// </para>
/// </summary>
public class StartupUpdatePreferenceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public StartupUpdatePreferenceTests() => _connection.Open();

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext()
        {
            DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
                .UseSqlite(connection)
                .Options;
            return new CSUploaderDbContext(options);
        }
    }

    private Factory Store(string? persisted)
    {
        Factory factory = new(_connection);
        using CSUploaderDbContext ctx = factory.CreateDbContext();
        ctx.Database.EnsureCreated();
        if (persisted is not null)
        {
            ctx.Settings.Add(new SettingDbm { Key = SettingKey.CheckForUpdatesAtStartup, Value = persisted });
            ctx.SaveChanges();
        }

        return factory;
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    public void APersistedPreference_IsRead(string stored, bool expected)
        => Assert.Equal(expected, StartupUpdatePreference.ReadCheckForUpdatesAtStartup(Store(stored)));

    /// <summary>
    /// Nothing stored is NOT the same as "off". A first run has no row, and the caller treats not
    /// knowing as the default — which is to ask. Returning false here would silently disable a
    /// feature nobody turned off.
    /// </summary>
    [Fact]
    public void WithNothingStored_TheAnswerIsUnknownRatherThanFalse()
        => Assert.Null(StartupUpdatePreference.ReadCheckForUpdatesAtStartup(Store(null)));

    /// <summary>
    /// The store being unreadable must not stop the app starting. This is the first-run and
    /// un-migrated-schema case, and it runs on the UI thread before anything is drawn.
    /// </summary>
    [Fact]
    public void WithNoDatabaseAtAll_ItReturnsNullRatherThanThrowing()
    {
        using SqliteConnection closed = new("Data Source=:memory:");

        // Never opened, so there is no schema and every query against it throws.
        Assert.Null(StartupUpdatePreference.ReadCheckForUpdatesAtStartup(new Factory(closed)));
    }

    /// <summary>
    /// A value that is neither "true" nor "false" is not a preference either, and gets the same
    /// answer a missing row does. Reading it as false would let one corrupt row silently disable a
    /// feature nobody turned off — the failure mode this whole null-versus-false distinction exists
    /// to avoid.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData(" false ")]
    [InlineData("  true  ")]
    public void AnUnparseableValue_IsUnknownRatherThanOff(string stored)
        => Assert.Null(StartupUpdatePreference.ReadCheckForUpdatesAtStartup(Store(stored)));
}
