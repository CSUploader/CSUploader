// <copyright file="DataGridColumnVisibilityPersistenceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.UI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ColumnState = CSUploader.Lib.UI.DataGridColumnVisibilityPersistence.ColumnState;

namespace CSUploader.Tests.Lib.UI;

/// <summary>
/// Tests target the DB-facing half of the helper (LoadOverridesAsync /
/// SaveOverridesAsync). The WPF-coupling ApplyAsync / PersistAsync wrappers are trivial
/// pass-throughs over the same primitives — exercising them would need an STA thread
/// to construct a real DataGrid, which isn't worth the harness.
/// </summary>
public class DataGridColumnVisibilityPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly SettingRepository _repo;

    public DataGridColumnVisibilityPersistenceTests()
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

        _repo = new SettingRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadOverridesAsync_NoSettingRow_ReturnsEmptyMap()
    {
        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "missing-key");

        Assert.Empty(map);
    }

    [Fact]
    public async Task LoadOverridesAsync_ParsesVisibilityAndDisplayIndex()
    {
        await _repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1|0,Size=0|3,URL=1|1" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");

        Assert.Equal(3, map.Count);
        Assert.Equal(new ColumnState(true, 0), map["Name"]);
        Assert.Equal(new ColumnState(false, 3), map["Size"]);
        Assert.Equal(new ColumnState(true, 1), map["URL"]);
    }

    [Fact]
    public async Task LoadOverridesAsync_LegacyVisibilityOnlyEntries_DefaultDisplayIndexToMinusOne()
    {
        // Older saves only stored visibility (no '|displayIndex'). Honour the visibility
        // and signal "leave display index alone" with -1 so ApplyAsync doesn't move the
        // column to position 0.
        await _repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1,Size=0" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");

        Assert.Equal(2, map.Count);
        Assert.Equal(new ColumnState(true, -1), map["Name"]);
        Assert.Equal(new ColumnState(false, -1), map["Size"]);
    }

    [Fact]
    public async Task LoadOverridesAsync_TolerantToMalformedEntries()
    {
        // Malformed pieces (missing '=', empty key, stray separators) get skipped
        // rather than crashing the load — the user's other persisted choices still apply.
        await _repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1|0,broken,=0|2,Size=0|1" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");

        Assert.Equal(2, map.Count);
        Assert.Equal(new ColumnState(true, 0), map["Name"]);
        Assert.Equal(new ColumnState(false, 1), map["Size"]);
    }

    [Fact]
    public async Task SaveOverridesAsync_Insert_RoundTripsState()
    {
        Dictionary<string, ColumnState> overrides = new(StringComparer.Ordinal)
        {
            ["Name"] = new(true, 0),
            ["Size"] = new(false, 2),
            ["URL"] = new(true, 1),
        };

        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", overrides);

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");
        Assert.Equal(overrides, parsed);
    }

    [Fact]
    public async Task SaveOverridesAsync_EmptyMap_WritesEmptyValue()
    {
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", new Dictionary<string, ColumnState>());

        SettingDto? row = await _repo.FindByKeyAsync("k");
        Assert.NotNull(row);
        Assert.Equal(string.Empty, row!.Value);
    }

    [Fact]
    public async Task SaveOverridesAsync_Update_OverwritesExistingValue()
    {
        await _repo.InsertAsync(new SettingDto { Key = "k", Value = "Old=1|0" });

        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["Size"] = new(false, 1),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");
        Assert.Equal(2, parsed.Count);
        Assert.True(parsed.ContainsKey("Name"));
        Assert.True(parsed.ContainsKey("Size"));
        Assert.False(parsed.ContainsKey("Old"));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsXamlDefaultCollapsedColumnSetVisible()
    {
        // Regression for the user-reported bug: the URL column ships with
        // Visibility=Collapsed in XAML. When the user turns it on, we have to remember
        // that explicit Visible state — otherwise on next launch it falls back to the
        // XAML default and the user sees no URL column.
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["URL"] = new(true, 5),    // user explicitly turned it on
            ["Speed"] = new(false, 2), // user explicitly turned it off
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");

        Assert.True(parsed["URL"].Visible, "user's explicit-show choice must survive a restart even when the XAML default is Collapsed");
        Assert.False(parsed["Speed"].Visible);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsDisplayIndex()
    {
        // User reorders the columns — the new positions have to survive a restart.
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["Size"] = new(true, 4),  // moved out
            ["ETA"] = new(true, 1),   // moved up
            ["URL"] = new(true, 2),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");

        Assert.Equal(0, parsed["Name"].DisplayIndex);
        Assert.Equal(1, parsed["ETA"].DisplayIndex);
        Assert.Equal(2, parsed["URL"].DisplayIndex);
        Assert.Equal(4, parsed["Size"].DisplayIndex);
    }

    [Fact]
    public async Task SaveOverridesAsync_FiltersOutEmptyHeaders()
    {
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(_repo, "k", new Dictionary<string, ColumnState>
        {
            ["Size"] = new(true, 0),
            [string.Empty] = new(false, 1),
            ["ETA"] = new(false, 2),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(_repo, "k");
        Assert.Equal(2, parsed.Count);
        Assert.False(parsed.ContainsKey(string.Empty));
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
