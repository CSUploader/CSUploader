// <copyright file="LogsViewTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Behaviors;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="LogsView"/> (Phase 5 Task 6): the four log grids over the
/// real <see cref="LogsViewModel"/>, the details-open path (Enter through the tunnel handler and the
/// double-tap hit-test), the <see cref="AutoScrollBehavior"/> first-consumer wiring, the rule-20 message cell
/// (trim + tooltip) and the rule-32 OneWay converter columns, plus the per-grid column-persistence menu. The
/// double-tap target resolution is driven through the <c>internal RowEntryFromSource</c> helper (raising a
/// real double-tap needs a synthesized pointer, the Phase 4 §8 sanctioned fallback); Enter is raised as a
/// genuine tunnel <see cref="InputElement.KeyDownEvent"/>, the UploadedView Ctrl+C precedent. Every shown
/// window is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class LogsViewTests
{
    // ── AddLogEntry routes each LogType to its own collection (Core VM, untouched) ──

    [AvaloniaFact]
    public void AddLogEntry_RoutesEachLogTypeToItsCollection()
    {
        LogsViewModel vm = new(Mock.Of<IDialogService>());

        vm.AddLogEntry(Event(LogType.Status, "s"));
        vm.AddLogEntry(Event(LogType.Http, "h"));
        vm.AddLogEntry(Event(LogType.Error, "e"));
        vm.AddLogEntry(Event(LogType.UI, "u"));

        Assert.Equal("s", Assert.Single(vm.StatusLogs).Message);
        Assert.Equal("h", Assert.Single(vm.HttpLogs).Message);
        Assert.Equal("e", Assert.Single(vm.ErrorLogs).Message);
        Assert.Equal("u", Assert.Single(vm.UILogs).Message);
    }

    // ── Enter on a selected row opens the right details window (port rule 23 tunnel handler) ──

    [AvaloniaFact]
    public void Enter_OnSelectedStatusRow_OpensLogDetailsWindow()
    {
        using VmHarness harness = new();
        harness.Vm.AddLogEntry(Event(LogType.Status, "a status line"));
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            view.StatusLogGrid.SelectedItem = harness.Vm.StatusLogs[0];
            Dispatcher.UIThread.RunJobs();

            RaiseEnter(view.StatusLogGrid);

            Assert.IsType<LogDetailsWindow>(view.LastDetailsWindow);
        }
        finally
        {
            view.LastDetailsWindow?.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter_OnSelectedHttpRow_OpensHttpDetailsWindow()
    {
        using VmHarness harness = new();
        harness.Vm.AddLogEntry(HttpEvent("https://example.test/v1/upload"));
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            SelectTab(view, 1); // realize the HTTP grid

            view.HttpLogGrid.SelectedItem = harness.Vm.HttpLogs[0];
            Dispatcher.UIThread.RunJobs();

            RaiseEnter(view.HttpLogGrid);

            Assert.IsType<HttpDetailsWindow>(view.LastDetailsWindow);
        }
        finally
        {
            view.LastDetailsWindow?.Close();
            window.Close();
        }
    }

    // ── Double-tap hit-test: a row resolves its entry; the header / empty area / grid do NOT (rule 22) ──

    [AvaloniaFact]
    public void DoubleTapTarget_ResolvesRow_ButNotHeaderOrEmptyOrGrid()
    {
        using VmHarness harness = new();
        harness.Vm.AddLogEntry(Event(LogType.Status, "double-tap me"));
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            DataGridRow row = RealizedRow(view.StatusLogGrid, harness.Vm.StatusLogs[0]);

            // A descendant of the row resolves to that row's entry (open what was clicked).
            Assert.Same(harness.Vm.StatusLogs[0], LogsView.RowEntryFromSource(row));

            // The column header, the grid itself and null resolve to nothing — no details open.
            DataGridColumnHeader header = view.StatusLogGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().First();
            Assert.Null(LogsView.RowEntryFromSource(header));
            Assert.Null(LogsView.RowEntryFromSource(view.StatusLogGrid));
            Assert.Null(LogsView.RowEntryFromSource(null));
        }
        finally
        {
            window.Close();
        }
    }

    // ── AutoScrollBehavior first-consumer proof: the bound AutoScroll drives one subscription on/off ──

    [AvaloniaFact]
    public void AutoScroll_WiredToBehavior_HoldsOneSubscription_ReleasedWhenToggledOff()
    {
        using VmHarness harness = new(); // AutoScroll defaults true
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.StatusLogGrid;
            Assert.True(AutoScrollBehavior.GetIsEnabled(grid));

            // Overflow the grid while following: the Add handler calls ScrollIntoView on each append and
            // must not throw (the functional half; the behavior internals are Phase 3-tested).
            for (int i = 0; i < 20; i++)
            {
                harness.Vm.AddLogEntry(Event(LogType.Status, $"line {i}"));
            }

            Dispatcher.UIThread.RunJobs();

            // The grid's own collection view subscribes too; the behavior's contribution is the delta of 1,
            // which must return to zero when the AutoScroll binding flips IsEnabled off.
            int withBehavior = CollectionChangedSubscriberCount(harness.Vm.StatusLogs);
            harness.Vm.AutoScroll = false;
            Dispatcher.UIThread.RunJobs();
            int without = CollectionChangedSubscriberCount(harness.Vm.StatusLogs);

            Assert.False(AutoScrollBehavior.GetIsEnabled(grid));
            Assert.Equal(without + 1, withBehavior);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 20: the message cell trims to one line and carries the full message as its tooltip ──

    [AvaloniaFact]
    public void MessageCell_TrimsToOneLine_AndCarriesFullMessageTooltip()
    {
        using VmHarness harness = new();
        harness.Vm.AddLogEntry(Event(LogType.Status, "a long message that should be trimmed with an ellipsis"));
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            DataGridCell cell = view.StatusLogGrid.GetVisualDescendants()
                .OfType<DataGridCell>()
                .First(c => c.Classes.Contains("msg-cell"));
            TextBlock text = cell.GetVisualDescendants().OfType<TextBlock>().First();

            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
            Assert.Equal(harness.Vm.StatusLogs[0].Message, ToolTip.GetTip(text));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 32: the throwing-ConvertBack columns are OneWay, so they render their Convert output and never
    //    write back. LogEntryViewModel is immutable, so the Task-5 source corruption cannot reproduce here;
    //    this asserts the positive render (the observable proof the OneWay converter binding works). ──

    [AvaloniaFact]
    public void ConverterColumns_RenderConvertedValues_ThroughOneWayBindings()
    {
        using VmHarness harness = new();
        harness.Vm.AddLogEntry(HttpEvent("https://example.test/a%20b?x=1%262"));
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            SelectTab(view, 1); // realize the HTTP grid (DateTime col 0 + URL col carry the converters)

            LogEntryViewModel entry = harness.Vm.HttpLogs[0];
            string expectedDate = (string)new DateTimeFormatConverter()
                .Convert(entry.DateTime, typeof(string), null, CultureInfo.InvariantCulture)!;
            string expectedUrl = (string)new UrlDecodeConverter()
                .Convert(entry.HttpTransaction!.Url, typeof(string), null, CultureInfo.InvariantCulture)!;

            var cellTexts = view.HttpLogGrid.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();

            Assert.Contains(expectedDate, cellTexts);            // "2026/07/11 10:00:01"
            Assert.Equal("https://example.test/a b?x=1&2", expectedUrl); // decoded (%20→space, %26→&)
            Assert.Contains(expectedUrl, cellTexts);

            // The immutable source is intact — no ConvertBack wrote through the read-only grid.
            Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 1), entry.DateTime);
            Assert.Equal("https://example.test/a%20b?x=1%262", entry.HttpTransaction!.Url);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Per-grid column-persistence menu: wired at first Loaded with the first (DateTime anchor) toggle off ──

    [AvaloniaFact]
    public void ColumnMenu_WiredPerGrid_WithFirstColumnToggleDisabled()
    {
        using VmHarness harness = new();
        (Window window, LogsView view) = Show(harness.Vm);
        try
        {
            // Status grid (default tab) wires on show.
            PumpUntil(() => view.ColumnMenuFor(view.StatusLogGrid) is not null);
            ContextMenu statusMenu = Assert.IsType<ContextMenu>(view.ColumnMenuFor(view.StatusLogGrid));
            Assert.False(Assert.IsType<MenuItem>(statusMenu.Items[0]).IsEnabled); // DateTime anchor stays visible

            // A second grid on a different tab wires with its own key when its tab is shown — the ×4 mechanism.
            SelectTab(view, 1);
            PumpUntil(() => view.ColumnMenuFor(view.HttpLogGrid) is not null);
            ContextMenu httpMenu = Assert.IsType<ContextMenu>(view.ColumnMenuFor(view.HttpLogGrid));
            Assert.False(Assert.IsType<MenuItem>(httpMenu.Items[0]).IsEnabled);
            Assert.NotSame(statusMenu, httpMenu);
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ──

    private static (Window Window, LogsView View) Show(LogsViewModel vm)
    {
        // Wide enough that the HTTP tab's ~1810px of columns are in the horizontal viewport when realized.
        LogsView view = new() { DataContext = vm };
        Window window = new() { Width = 2000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static void SelectTab(LogsView view, int index)
    {
        TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    private static void RaiseEnter(DataGrid grid) =>
        grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static DataGridRow RealizedRow(DataGrid grid, object item)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));

    private static void PumpUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static int CollectionChangedSubscriberCount(INotifyCollectionChanged source)
    {
        FieldInfo field = source.GetType().GetField("CollectionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (NotifyCollectionChangedEventHandler?)field.GetValue(source);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private static LogEvent Event(LogType type, string message) => new()
    {
        LogType = type,
        DateTime = new DateTime(2026, 7, 11, 10, 0, 0),
        ThreadId = 3,
        Filename = "FileHosterClient.cs",
        Function = "UploadAsync",
        LineNumber = 42,
        Message = message,
    };

    private static LogEvent HttpEvent(string url)
    {
        DateTime start = new(2026, 7, 11, 10, 0, 1);
        return new LogEvent
        {
            LogType = LogType.Http,
            DateTime = start,
            ThreadId = 4,
            Filename = "HttpHandler.cs",
            Function = "SendAsync",
            LineNumber = 88,
            Message = "GET",
            HttpTransaction = new HttpTransaction
            {
                Method = "GET",
                Url = url,
                Proxy = "direct",
                StatusCode = 200,
                StatusReason = "OK",
                StartTime = start,
                EndTime = start.AddMilliseconds(120),
                RequestHeaders = new Dictionary<string, string[]> { ["User-Agent"] = ["CSUploader/0.0.6"] },
                ResponseHeaders = new Dictionary<string, string[]> { ["Server"] = ["nginx"] },
            },
        };
    }

    /// <summary>Real Sqlite-backed <see cref="LogsViewModel"/> — the column-persistence path needs a live
    /// <see cref="SettingRepository"/> (its NOCASE collation can't run on the EF InMemory provider).</summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public VmHarness()
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

            Vm = new LogsViewModel(Mock.Of<IDialogService>(), new SettingRepository(factory));
        }

        public LogsViewModel Vm { get; }

        public void Dispose() => _connection.Dispose();

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
