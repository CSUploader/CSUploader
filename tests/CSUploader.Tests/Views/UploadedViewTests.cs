// <copyright file="UploadedViewTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using static CSUploader.Tests.Avalonia.LeakProbes;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="UploadedView"/> (Phase 5 Task 5): the grouped-DataGrid
/// recipe the Task 2 probe pinned (now over the real VM collection), the URL-cell visibility rule, the
/// right-click row/group targeting, the context-menu suppression + selection snapshot, the built-in Ctrl+C,
/// the Delete key binding and the column menu. The four grouping/collapse/copy/index checks are the
/// retargeted probe tests; <see cref="Rebuild_ReExpandsAllGroups"/> is the LoadAsync-parity keystone. Every
/// shown window is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class UploadedViewTests
{
    // ── Checklist 1: the grouped view groups correctly (built in the view code-behind) ──

    [AvaloniaFact]
    public void BuildGroupedView_GroupsByPackageName_ThreeGroupsWithMatchingKeysAndCounts()
    {
        DataGridCollectionView view = UploadedView.BuildGroupedView(FixtureRows());

        Assert.Equal(3, view.Groups.Count);

        var groups = view.Groups.Cast<DataGridCollectionViewGroup>().ToList();
        Assert.Equal(
            new[] { "Fake pack (photos)", "Fake pack (documents)", "Fake pack (archive set)" },
            groups.Select(g => (string)g.Key));
        Assert.Equal(new[] { 3, 2, 2 }, groups.Select(g => g.ItemCount));
    }

    // ── Checklist 3: collapse hides the group's rows; re-expand restores them ──

    [AvaloniaFact]
    public void CollapseRowGroup_HidesGroupRows_ExpandRestores()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.FilesGrid;
            var view2 = (DataGridCollectionView)grid.ItemsSource!;
            var firstGroup = (DataGridCollectionViewGroup)view2.Groups[0]; // photos — 3 rows

            Assert.Equal(7, RealizedRowCount(grid));

            grid.CollapseRowGroup(firstGroup, collapseAllSubgroups: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, RealizedRowCount(grid));

            grid.ExpandRowGroup(firstGroup, expandAllSubgroups: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(7, RealizedRowCount(grid));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Repro: right-click a group header through the REAL pointer chain (hit-test + tunnel handler),
    //    not the direct ApplyRightClickSelection call the other targeting tests use. Field report: on the
    //    History tab, right-clicking a package selected nothing and the menu's Remove no-oped. ──

    [AvaloniaFact]
    public void RightClick_OnGroupHeader_RealPointerEvent_SelectsTheWholeGroup()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.FilesGrid;
            DataGridRowGroupHeader header = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>().First();
            Point centre = header.TranslatePoint(
                new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window) ?? default;

            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            // The DataGrid's OWN press handling cleared the tunnel handler's selection here (observed as
            // +3 then -3 in SelectionChanged) — the fix re-applies the intended targets when the menu
            // opens. Drive the Opening path exactly as the ContextMenu does.
            bool suppressed = view.SnapshotSelectionAndDecideSuppression();

            // The first (photos) group has 3 rows — the menu must open (not suppressed) over all of them.
            Assert.False(suppressed);
            Assert.Equal(3, grid.SelectedItems.Count);
            Assert.Equal(3, harness.Vm.SelectedRows.Count); // the snapshot the commands act on
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 4: built-in Ctrl+C on the grouped view (row-copy menu raises this synthetic key) ──

    [AvaloniaFact]
    public async Task CtrlC_OnGroupedView_CopiesHeaderPlusSelectedRows_NoGroupHeaderPollution()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.FilesGrid;

            grid.SelectedItems.Add(harness.Vm.Files[0]); // photos / fake_beach.jpg
            grid.SelectedItems.Add(harness.Vm.Files[1]); // photos / fake_sunset.png
            Dispatcher.UIThread.RunJobs();

            // The row-copy menu item raises exactly this synthetic Ctrl+C; assert it reaches the copy path.
            grid.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.C,
                KeyModifiers = KeyModifiers.Control,
            });
            Dispatcher.UIThread.RunJobs();

            string? clip = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
            Assert.NotNull(clip);
            Assert.Contains("\"Name\"", clip!, StringComparison.Ordinal); // IncludeHeader row
            Assert.Contains("fake_beach.jpg", clip!, StringComparison.Ordinal);
            Assert.Contains("fake_sunset.png", clip!, StringComparison.Ordinal);
            // The copy iterates SelectedItems (data rows) — group headers must NOT pollute it.
            Assert.DoesNotContain("Fake pack", clip!, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Checklist 7: the zebra index basis numbers file rows flat across groups ──

    [AvaloniaFact]
    public void RowIndex_NumbersFileRowsFlatAcrossGroups()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            var byItem = view.FilesGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(r => r.DataContext is UploadedFileRow)
                .ToDictionary(r => ((UploadedFileRow)r.DataContext!).FileName, r => r.Index);

            Assert.Equal(7, byItem.Count);
            Assert.Equal(0, byItem["fake_beach.jpg"]);   // group 0, row 0
            Assert.Equal(2, byItem["fake_pano.raw"]);    // group 0, row 2
            Assert.Equal(3, byItem["fake_report.pdf"]);  // group 1, row 0 → flat 3 (no header gap)
            Assert.Equal(6, byItem["fake_part2.rar"]);   // group 2, row 1 → flat 6
        }
        finally
        {
            window.Close();
        }
    }

    // ── URL cell hides on null/empty FileUrl (port rule 25) ──

    [AvaloniaFact]
    public void UrlCell_HiddenForEmptyOrNullUrl_VisibleOtherwise()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            // Correlate each row with its URL-cell TextBlock (a null/empty FileUrl hides it via rule 25).
            Dictionary<string, bool> visibleByFile = new();
            foreach (DataGridRow row in view.FilesGrid.GetVisualDescendants()
                         .OfType<DataGridRow>()
                         .Where(r => r.DataContext is UploadedFileRow))
            {
                TextBlock? url = row.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(tb => tb.Classes.Contains("url-link"));
                string name = ((UploadedFileRow)row.DataContext!).FileName;

                // A hidden cell is "not visible" whether Avalonia keeps a collapsed TextBlock or omits it.
                visibleByFile[name] = url is { IsVisible: true };
            }

            Assert.False(visibleByFile["fake_pano.raw"]);   // null FileUrl → hidden
            Assert.True(visibleByFile["fake_beach.jpg"]);   // has URL → visible
            Assert.True(visibleByFile["fake_report.pdf"]);  // has URL → visible
        }
        finally
        {
            window.Close();
        }
    }

    // ── Right-click targeting (prep item 12; view code-behind) ──

    [AvaloniaFact]
    public void RightClick_UnselectedRow_ExclusiveSelectsIt()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGridRow target = RowFor(view.FilesGrid, "fake_report.pdf");

            view.ApplyRightClickSelection(target);

            Assert.Single(view.FilesGrid.SelectedItems);
            Assert.Same(harness.Vm.Files.Single(r => r.FileName == "fake_report.pdf"), view.FilesGrid.SelectedItems[0]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClick_InsideMultiSelection_PreservesIt()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            view.FilesGrid.SelectedItems.Add(harness.Vm.Files[0]);
            view.FilesGrid.SelectedItems.Add(harness.Vm.Files[1]);

            // Right-click on one of the already-selected rows must keep the whole selection (Explorer UX).
            view.ApplyRightClickSelection(RowFor(view.FilesGrid, harness.Vm.Files[0].FileName));

            Assert.Equal(2, view.FilesGrid.SelectedItems.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClick_GroupHeader_SelectsWholeGroup()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGridRowGroupHeader header = view.FilesGrid.GetVisualDescendants()
                .OfType<DataGridRowGroupHeader>()
                .First(h => h.DataContext is DataGridCollectionViewGroup { Key: "Fake pack (photos)" });

            view.ApplyRightClickSelection(header);

            Assert.Equal(3, view.FilesGrid.SelectedItems.Count); // photos group has 3 files
            Assert.All(
                view.FilesGrid.SelectedItems.Cast<UploadedFileRow>(),
                r => Assert.Equal("Fake pack (photos)", r.PackageName));
        }
        finally
        {
            window.Close();
        }
    }

    // ── ContextMenu.Opening: suppress on empty space, snapshot the multi-selection otherwise ──

    [AvaloniaFact]
    public void Opening_OnEmptySpace_SuppressesMenu_OnRow_SnapshotsSelectionAndOpens()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            // Empty-space right-click (source is the grid, not a row/header) → suppress.
            view.ApplyRightClickSelection(view.FilesGrid);
            Assert.True(view.SnapshotSelectionAndDecideSuppression());

            // Right-click a row → do not suppress, and the VM's SelectedRows snapshot is taken.
            view.ApplyRightClickSelection(RowFor(view.FilesGrid, "fake_specs.docx"));
            Assert.False(view.SnapshotSelectionAndDecideSuppression());
            Assert.Single(harness.Vm.SelectedRows);
            Assert.Equal("fake_specs.docx", harness.Vm.SelectedRows[0].FileName);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Delete key binding wired to RemoveSelectedCommand with the live SelectedItems parameter ──

    [AvaloniaFact]
    public void DeleteKeyBinding_WiredToRemoveSelectedCommand_WithSelectedItemsParameter()
    {
        using VmHarness harness = new();
        UploadedView view = new() { DataContext = harness.Vm };

        KeyBinding binding = Assert.Single(view.FilesGrid.KeyBindings);
        Assert.Equal(Key.Delete, binding.Gesture.Key);
        Assert.Same(harness.Vm.RemoveSelectedCommand, binding.Command);
        Assert.Same(view.FilesGrid.SelectedItems, binding.CommandParameter);
    }

    // ── Column menu attached, with the first (anchor) column toggle disabled ──

    [AvaloniaFact]
    public void ColumnMenu_AttachedWithFirstItemDisabled()
    {
        using VmHarness harness = new();
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            PumpUntil(() => view.ColumnMenu is not null);

            Assert.NotNull(view.ColumnMenu);
            var first = Assert.IsType<MenuItem>(view.ColumnMenu!.Items[0]);
            Assert.False(first.IsEnabled); // the Name column stays visible — the group expander lives there
        }
        finally
        {
            window.Close();
        }
    }

    // ── Read-only columns must not write back through their (throwing) converters ──

    [AvaloniaFact]
    public void ReadOnlyConverterColumns_DoNotWriteBack_SourceValuesSurviveBinding()
    {
        // Avalonia's DataGridTextColumn.Binding defaults to TwoWay and pushes the ConvertBack result to
        // the source on bind — even in a read-only grid. ByteUnitConverter/DateTimeFormatConverter throw on
        // ConvertBack, so without Mode=OneWay the bind would blank FileSize (→0) and the dates (→MinValue).
        // This asserts the source values survive the grid binding.
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        long[] sizesBefore = [.. harness.Vm.Files.Select(r => r.FileSize)];
        DateTime[] finishedBefore = [.. harness.Vm.Files.Select(r => r.FinishedDateTime)];

        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            long[] sizesAfter = [.. harness.Vm.Files.Select(r => r.FileSize)];
            DateTime[] finishedAfter = [.. harness.Vm.Files.Select(r => r.FinishedDateTime)];
            Assert.Equal(sizesBefore, sizesAfter);
            Assert.Equal(finishedBefore, finishedAfter);
            Assert.All(harness.Vm.Files, r => Assert.True(r.FileSize > 0));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Keystone: a full rebuild (LoadAsync Clear + re-add) re-expands every group ──

    [AvaloniaFact]
    public void Rebuild_ReExpandsAllGroups()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            DataGrid grid = view.FilesGrid;
            var firstGroup = (DataGridCollectionViewGroup)((DataGridCollectionView)grid.ItemsSource!).Groups[0];

            grid.CollapseRowGroup(firstGroup, collapseAllSubgroups: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, RealizedRowCount(grid)); // group 0's 3 rows collapsed

            // Mirror UploadedViewModel.LoadAsync: clear then re-add on the same collection.
            harness.Vm.Files.Clear();
            foreach (UploadedFileRow row in FixtureRows())
            {
                harness.Vm.Files.Add(row);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(7, RealizedRowCount(grid)); // all groups back, expanded
        }
        finally
        {
            window.Close();
        }
    }

    // ── Leak regression: reloads must not accumulate subscribers on the long-lived Files collection ──

    [AvaloniaFact]
    public void Reload_KeepsOneDurableView_DoesNotAccumulateFilesSubscribers()
    {
        using VmHarness harness = new();
        SeedFixture(harness.Vm);
        (Window window, UploadedView view) = Show(harness.Vm);
        try
        {
            // After the first bind, only the durable DataGridCollectionView and the view's own Reset handler
            // subscribe to Files.CollectionChanged. The pre-fix code minted a fresh DataGridCollectionView on
            // every LoadAsync reload; that view is never disposed (the type is not IDisposable) so it never
            // unsubscribes, and the count grew by one per reload — the unbounded leak this pins. The durable
            // view keeps both the count and the ItemsSource instance flat across any number of reloads.
            object viewBefore = view.FilesGrid.ItemsSource!;
            int baseline = CollectionChangedSubscriberCount(harness.Vm.Files);

            for (int cycle = 0; cycle < 5; cycle++)
            {
                harness.Vm.Files.Clear();
                foreach (UploadedFileRow row in FixtureRows())
                {
                    harness.Vm.Files.Add(row);
                }

                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(baseline, CollectionChangedSubscriberCount(harness.Vm.Files));
            Assert.Same(viewBefore, view.FilesGrid.ItemsSource); // no new view minted
            Assert.Equal(7, RealizedRowCount(view.FilesGrid));   // still shows every row, expanded
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ──

    private static int RealizedRowCount(DataGrid grid)
        => grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);

    private static DataGridRow RowFor(DataGrid grid, string fileName)
        => grid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .First(r => r.DataContext is UploadedFileRow row && row.FileName == fileName);

    private static (Window Window, UploadedView View) Show(UploadedViewModel vm)
    {
        // Wide enough that every column (~2150px total) is in the horizontal viewport — the DataGrid
        // virtualizes columns, so a narrower window would leave the trailing URL cells unrealized.
        UploadedView view = new() { DataContext = vm };
        Window window = new() { Width = 2400, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void SeedFixture(UploadedViewModel vm)
    {
        foreach (UploadedFileRow row in FixtureRows())
        {
            vm.Files.Add(row);
        }
    }

    // Three package groups of uneven size, one null URL — the UploadedView shape the probe pinned. Neutral
    // example.test URLs (the probe's Defender-safe convention; no test asserts a URL host).
    private static ObservableCollection<UploadedFileRow> FixtureRows() =>
    [
        Row("Fake pack (photos)", "fake_beach.jpg", 1_000_000, "https://example.test/dl/fake02.jpg", "Catbox"),
        Row("Fake pack (photos)", "fake_sunset.png", 2_000_000, "https://example.test/dl/fake03.png", "Catbox"),
        Row("Fake pack (photos)", "fake_pano.raw", 2_000_000, url: null, "Rapidgator"),
        Row("Fake pack (documents)", "fake_report.pdf", 1_000_000, "https://example.test/dl/fake04.pdf", "Rapidgator"),
        Row("Fake pack (documents)", "fake_specs.docx", 1_000_000, "https://example.test/dl/fake05.docx", "Catbox"),
        Row("Fake pack (archive set)", "fake_part1.rar", 3_000_000, "https://example.test/dl/fake06.rar", "Rapidgator"),
        Row("Fake pack (archive set)", "fake_part2.rar", 3_000_000, "https://example.test/dl/fake07.rar", "Catbox"),
    ];

    private static UploadedFileRow Row(string package, string name, long size, string? url, string hoster) => new()
    {
        PackageName = package,
        FileName = name,
        FileSize = size,
        FileUrl = url,
        FileHosterName = hoster,
        FileDirectory = "C:\\downloads",
        AccountDisplay = "fake_user",
        FinishedDateTime = new DateTime(2026, 7, 11, 10, 0, 0),
        StartedDateTime = new DateTime(2026, 7, 11, 9, 0, 0),
        FileHash = "0123456789abcdef",
    };

    /// <summary>
    /// Builds a real <see cref="UploadedViewModel"/> over an in-memory SQLite DB — the same scratch-repo
    /// harness the WPF <c>UploadedViewModelTests</c> uses (no new packages; Core's EF Sqlite flows
    /// transitively). The view tests populate <see cref="UploadedViewModel.Files"/> directly, so the DB stays
    /// empty; <see cref="SettingRepo"/> backs the column-persistence path.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UploadScheduler _scheduler;

        public VmHarness(IDialogService? dialogService = null)
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

            UploadPackageFileRepository fileRepo = new(factory);
            UploadPackageRepository packageRepo = new(factory);
            SettingRepository settingRepo = new(factory);

            AppSettings settings = new();
            IAppLogger logger = Mock.Of<IAppLogger>();
            FileHosterLoginRepository loginRepo = new(factory);
            DefaultFileHosterRegistry registry = new([]);
            _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
            PackageManager packageManager = new(settings, _scheduler, packageRepo, fileRepo, loginRepo, logger, registry);

            Vm = new UploadedViewModel(
                packageRepo,
                fileRepo,
                packageManager,
                dialogService ?? Mock.Of<IDialogService>(),
                Mock.Of<IAppLogger>(),
                Mock.Of<IUiDispatcher>(),
                Mock.Of<IClipboardService>(),
                settingRepo);
        }

        public UploadedViewModel Vm { get; }

        public void Dispose()
        {
            _scheduler.Dispose();
            _connection.Dispose();
        }

        private static AttemptRunner BuildAttemptRunner()
        {
            DefaultFileHosterRegistry registry = new([]);
            Mock<IProxySource> proxy = new();
            proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
            Mock<IHttpHandlerFactory> hf = new();
            hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
                .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
            return new AttemptRunner(registry, proxy.Object, hf.Object);
        }

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
