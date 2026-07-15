// <copyright file="DataGridColumnPersistenceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Dal;
using CSUploader.Lib.UI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ColumnState = CSUploader.Lib.UI.DataGridColumnVisibilityPersistence.ColumnState;

namespace CSUploader.Tests.Avalonia.Lib;

/// <summary>
/// In-memory Sqlite-backed <see cref="SettingRepository"/> harness — the exact shape the WPF
/// suite uses (tests/Lib/UI/DataGridColumnVisibilityPersistenceTests.cs). Real Sqlite is
/// mandatory: <see cref="SettingRepository.FindByKeyAsync"/> translates
/// <c>EF.Functions.Collate(..., "NOCASE")</c>, which the EF InMemory provider can't run.
/// </summary>
public abstract class SqliteSettingHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    protected SqliteSettingHarness()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        IDbContextFactory<CSUploaderDbContext> factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        Repo = new SettingRepository(factory);
    }

    protected SettingRepository Repo { get; }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}

/// <summary>
/// Row item for the headless grid harness — the grids only read column Header/IsVisible/
/// DisplayIndex, so a single bound string suffices.
/// </summary>
public sealed record GridTestRow(string Name);

/// <summary>
/// Builds a realized <see cref="DataGrid"/> hosted in a shown window so DisplayIndex is
/// finalized (mirrors the GroupingProbe harness: Show + RunJobs). Callers close the window.
/// </summary>
internal static class GridTestFactory
{
    public static (Window Window, DataGrid Grid) BuildShownGrid(params string[] headers)
    {
        DataGrid grid = new();
        foreach (string header in headers)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(nameof(GridTestRow.Name)) });
        }

        grid.ItemsSource = new[] { new GridTestRow("r1"), new GridTestRow("r2") };
        Window window = new() { Width = 500, Height = 300, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid);
    }
}

/// <summary>
/// The persisted-format vectors, DUPLICATED verbatim from the WPF suite
/// (tests/Lib/UI/DataGridColumnVisibilityPersistenceTests.cs). Both heads read the same
/// Setting rows through byte-identical Load/Save halves; keeping the vectors on both sides
/// means a drift on either side breaks that side's tests (the plan's format-drift guard).
/// </summary>
public class DataGridColumnPersistenceFormatTests : SqliteSettingHarness
{
    [Fact]
    public async Task LoadOverridesAsync_NoSettingRow_ReturnsEmptyMap()
    {
        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "missing-key");

        Assert.Empty(map);
    }

    [Fact]
    public async Task LoadOverridesAsync_ParsesVisibilityAndDisplayIndex()
    {
        await Repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1|0,Size=0|3,URL=1|1" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");

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
        await Repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1,Size=0" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");

        Assert.Equal(2, map.Count);
        Assert.Equal(new ColumnState(true, -1), map["Name"]);
        Assert.Equal(new ColumnState(false, -1), map["Size"]);
    }

    [Fact]
    public async Task LoadOverridesAsync_TolerantToMalformedEntries()
    {
        // Malformed pieces (missing '=', empty key, stray separators) get skipped
        // rather than crashing the load — the user's other persisted choices still apply.
        await Repo.InsertAsync(new SettingDto { Key = "k", Value = "Name=1|0,broken,=0|2,Size=0|1" });

        Dictionary<string, ColumnState> map = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");

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

        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", overrides);

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
        Assert.Equal(overrides, parsed);
    }

    [Fact]
    public async Task SaveOverridesAsync_EmptyMap_WritesEmptyValue()
    {
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>());

        SettingDto? row = await Repo.FindByKeyAsync("k");
        Assert.NotNull(row);
        Assert.Equal(string.Empty, row!.Value);
    }

    [Fact]
    public async Task SaveOverridesAsync_Update_OverwritesExistingValue()
    {
        await Repo.InsertAsync(new SettingDto { Key = "k", Value = "Old=1|0" });

        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["Size"] = new(false, 1),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
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
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["URL"] = new(true, 5),    // user explicitly turned it on
            ["Speed"] = new(false, 2), // user explicitly turned it off
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");

        Assert.True(parsed["URL"].Visible, "user's explicit-show choice must survive a restart even when the XAML default is Collapsed");
        Assert.False(parsed["Speed"].Visible);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsDisplayIndex()
    {
        // User reorders the columns — the new positions have to survive a restart.
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>
        {
            ["Name"] = new(true, 0),
            ["Size"] = new(true, 4),  // moved out
            ["ETA"] = new(true, 1),   // moved up
            ["URL"] = new(true, 2),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");

        Assert.Equal(0, parsed["Name"].DisplayIndex);
        Assert.Equal(1, parsed["ETA"].DisplayIndex);
        Assert.Equal(2, parsed["URL"].DisplayIndex);
        Assert.Equal(4, parsed["Size"].DisplayIndex);
    }

    [Fact]
    public async Task SaveOverridesAsync_FiltersOutEmptyHeaders()
    {
        await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>
        {
            ["Size"] = new(true, 0),
            [string.Empty] = new(false, 1),
            ["ETA"] = new(false, 2),
        });

        Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
        Assert.Equal(2, parsed.Count);
        Assert.False(parsed.ContainsKey(string.Empty));
    }
}

/// <summary>
/// The grid-facing members over a real (headless) Avalonia <see cref="DataGrid"/> — the half
/// the WPF suite couldn't exercise (it would have needed an STA WPF grid). These pin the
/// Avalonia re-implementation of CaptureCurrentState / ApplyAsync / ResetAsync / PersistAsync
/// against the same persisted format.
/// </summary>
public class DataGridColumnPersistenceGridTests : SqliteSettingHarness
{
    [AvaloniaFact]
    public void CaptureCurrentState_MapsHeadersToVisibilityAndCollectionIndex()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            grid.Columns[2].IsVisible = false; // hide C

            Dictionary<string, ColumnState> map = DataGridColumnVisibilityPersistence.CaptureCurrentState(grid);

            Assert.Equal(4, map.Count);
            Assert.Equal(new ColumnState(true, 0), map["A"]);
            Assert.Equal(new ColumnState(true, 1), map["B"]);
            Assert.Equal(new ColumnState(false, 2), map["C"]); // collection index, not DisplayIndex
            Assert.Equal(new ColumnState(true, 3), map["D"]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ApplyAsync_HidesOverriddenColumn_AndReordersPerDisplayIndex()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            // Reverse the order and hide B.
            await DataGridColumnVisibilityPersistence.SaveOverridesAsync(Repo, "k", new Dictionary<string, ColumnState>
            {
                ["A"] = new(true, 3),
                ["B"] = new(false, 2),
                ["C"] = new(true, 1),
                ["D"] = new(true, 0),
            });

            await DataGridColumnVisibilityPersistence.ApplyAsync(grid, Repo, "k");
            Dispatcher.UIThread.RunJobs();

            Assert.False(grid.Columns[1].IsVisible); // B hidden
            Assert.Equal(0, grid.Columns[3].DisplayIndex); // D → 0
            Assert.Equal(1, grid.Columns[2].DisplayIndex); // C → 1
            Assert.Equal(2, grid.Columns[1].DisplayIndex); // B → 2
            Assert.Equal(3, grid.Columns[0].DisplayIndex); // A → 3
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PersistAsync_WritesCurrentVisibilityAndOrder()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            grid.Columns[2].IsVisible = false; // hide C
            Dispatcher.UIThread.RunJobs();

            await DataGridColumnVisibilityPersistence.PersistAsync(grid, Repo, "k");

            Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
            Assert.Equal(new ColumnState(true, 0), parsed["A"]);
            Assert.Equal(new ColumnState(true, 1), parsed["B"]);
            Assert.Equal(new ColumnState(false, 2), parsed["C"]);
            Assert.Equal(new ColumnState(true, 3), parsed["D"]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResetAsync_RestoresDefaults_AndClearsPersistedRow()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            // Snapshot the XAML defaults (all visible, collection order) BEFORE mutating.
            Dictionary<string, ColumnState> defaults = DataGridColumnVisibilityPersistence.CaptureCurrentState(grid);

            // Mutate: hide B, move D to the front, and persist that state.
            grid.Columns[1].IsVisible = false;
            grid.Columns[3].DisplayIndex = 0;
            Dispatcher.UIThread.RunJobs();
            await DataGridColumnVisibilityPersistence.PersistAsync(grid, Repo, "k");

            await DataGridColumnVisibilityPersistence.ResetAsync(grid, defaults, Repo, "k");
            Dispatcher.UIThread.RunJobs();

            Assert.True(grid.Columns[1].IsVisible); // B visible again
            Assert.Equal(0, grid.Columns[0].DisplayIndex); // A back to 0
            Assert.Equal(1, grid.Columns[1].DisplayIndex);
            Assert.Equal(2, grid.Columns[2].DisplayIndex);
            Assert.Equal(3, grid.Columns[3].DisplayIndex);

            // The persisted row is cleared so the next launch's ApplyAsync is a no-op.
            Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
            Assert.Empty(parsed);
        }
        finally
        {
            window.Close();
        }
    }
}
