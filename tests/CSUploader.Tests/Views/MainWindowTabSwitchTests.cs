// <copyright file="MainWindowTabSwitchTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Update;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static CSUploader.Tests.Avalonia.LeakProbes;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Switching tabs must leave each tab's view alone. A DataContext binding follows the VISUAL parent, and leaving
/// a tab takes its view out of the visual tree, so with the binding on the view every tab switch turned that
/// view's DataContext to null and back. Each grid then came back unselected and scrolled to the top — the
/// Settings and Logs grids unsorted too — and every grid but History's got a new DataGridCollectionView,
/// which subscribes to its collection for good: an orphaned view per grid per visit, four per Logs visit.
/// These drive the real MainWindow over the real view-model graph.
/// </summary>
public class MainWindowTabSwitchTests
{
    private const int UploadsTab = 0;
    private const int HistoryTab = 1;
    private const int SettingsTab = 2;
    private const int LogsTab = 3;
    private const int ConnectionCategory = 2;
    private const int AccountsCategory = 3;

    [AvaloniaTheory]
    [InlineData(UploadsTab)]
    [InlineData(HistoryTab)]
    [InlineData(SettingsTab)]
    [InlineData(LogsTab)]
    public void LeavingATabAndComingBack_LeavesItsViewsDataContextInPlace(int tab)
    {
        using Shell shell = new();
        object expected = tab switch
        {
            UploadsTab => shell.Vm.UploadsViewModel,
            HistoryTab => shell.Vm.UploadedViewModel,
            SettingsTab => shell.Vm.SettingsViewModel,
            LogsTab => shell.Vm.LogsViewModel,
            _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, null),
        };
        Control view = shell.ViewOn(tab);
        Assert.Same(expected, view.DataContext);
        int changes = 0;
        view.DataContextChanged += (_, _) => changes++;

        shell.Select(OtherThan(tab));
        Assert.Same(expected, view.DataContext); // still its view-model while another tab shows
        shell.Select(tab);

        Assert.Equal(0, changes);
        Assert.Same(expected, view.DataContext);
    }

    // Each case names the grid and the collection it must show, so a grid left unbound cannot pass as
    // "nothing leaked". The Uploads and History rows guard the end-to-end rule rather than one fix: both
    // views keep one DataGridCollectionView per view-model, which hides a DataContext bounce from these
    // counts — the theory above is what catches the bounce itself.
    [AvaloniaTheory]
    [InlineData("Uploads")]
    [InlineData("History")]
    [InlineData("Settings accounts")]
    [InlineData("Settings proxies")]
    [InlineData("Logs status")]
    [InlineData("Logs HTTP")]
    [InlineData("Logs errors")]
    [InlineData("Logs UI")]
    public void TabRoundTrips_AddNoSubscribersToTheCollectionsTheGridsShow(string grid)
    {
        using Shell shell = new();
        MainViewModel vm = shell.Vm;
        (int Tab, int? Category, Func<Control, DataGrid> Grid, INotifyCollectionChanged Source) target = grid switch
        {
            "Uploads" => (UploadsTab, null, v => ((UploadsView)v).uploadsGrid, vm.UploadsViewModel.VisibleRows),
            "History" => (HistoryTab, null, v => ((UploadedView)v).FilesGrid, vm.UploadedViewModel.Files),
            "Settings accounts" => (SettingsTab, AccountsCategory, v => ((SettingsView)v).accountsGrid, vm.SettingsViewModel.AccountManager.Accounts),
            "Settings proxies" => (SettingsTab, ConnectionCategory, v => ((SettingsView)v).ProxyGrid, vm.ConnectionManagerViewModel.Proxies),
            "Logs status" => (LogsTab, null, v => ((LogsView)v).StatusLogGrid, vm.LogsViewModel.StatusLogs),
            "Logs HTTP" => (LogsTab, null, v => ((LogsView)v).HttpLogGrid, vm.LogsViewModel.HttpLogs),
            "Logs errors" => (LogsTab, null, v => ((LogsView)v).ErrorLogGrid, vm.LogsViewModel.ErrorLogs),
            "Logs UI" => (LogsTab, null, v => ((LogsView)v).UILogGrid, vm.LogsViewModel.UILogs),
            _ => throw new ArgumentOutOfRangeException(nameof(grid), grid, null),
        };
        (int tab, int? category, Func<Control, DataGrid> pick, INotifyCollectionChanged source) = target;

        if (category is { } settingsCategory)
        {
            vm.SettingsViewModel.SelectedCategoryIndex = settingsCategory;
        }

        DataGrid shown = pick(shell.ViewOn(tab));
        IDataGridCollectionView viewBefore = shown.CollectionView;
        Assert.Same(source, Assert.IsType<DataGridCollectionView>(viewBefore).SourceCollection);
        int baseline = CollectionChangedSubscriberCount(source);

        for (int i = 0; i < 5; i++)
        {
            shell.Select(OtherThan(tab));
            shell.Select(tab);
        }

        Assert.Same(viewBefore, shown.CollectionView);
        Assert.Equal(baseline, CollectionChangedSubscriberCount(source));
    }

    /// <summary>
    /// What the stable DataContext buys the user: a grid keeps its view across a visit to another tab, and
    /// with it the column sort and the selection. Both used to come back reset.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Settings proxies")]
    [InlineData("Logs status")]
    public void Grids_KeepTheirSortAndSelection_AcrossATabRoundTrip(string grid)
    {
        using Shell shell = new();
        MainViewModel vm = shell.Vm;
        int tab;
        DataGrid shown;
        object selected;
        string sortPath;
        if (grid == "Settings proxies")
        {
            foreach (string host in new[] { "c.example", "a.example", "b.example" })
            {
                vm.ConnectionManagerViewModel.Proxies.Add(new ProxySettingItem(
                    new ProxySettingDto { Host = host, Port = 8080, Type = ProxyType.Http, Enabled = true }));
            }

            vm.SettingsViewModel.SelectedCategoryIndex = ConnectionCategory;
            tab = SettingsTab;
            shown = ((SettingsView)shell.ViewOn(tab)).ProxyGrid;
            selected = vm.ConnectionManagerViewModel.Proxies[1];
            sortPath = nameof(ProxySettingItem.Host);
        }
        else
        {
            vm.LogsViewModel.AutoScroll = false;
            foreach (string message in new[] { "c", "a", "b" })
            {
                vm.LogsViewModel.StatusLogs.Add(new LogEntryViewModel(new LogEvent { Message = message }));
            }

            tab = LogsTab;
            shown = ((LogsView)shell.ViewOn(tab)).StatusLogGrid;
            selected = vm.LogsViewModel.StatusLogs[1];
            sortPath = nameof(LogEntryViewModel.Message);
        }

        IDataGridCollectionView view = shown.CollectionView;
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(sortPath, ListSortDirection.Ascending));
        shown.SelectedItem = selected;
        Dispatcher.UIThread.RunJobs();

        shell.Select(OtherThan(tab));
        shell.Select(tab);

        Assert.Same(view, shown.CollectionView);
        Assert.Single(view.SortDescriptions);
        Assert.Same(selected, shown.SelectedItem);
    }

    /// <summary>
    /// A grid scrolled down is still scrolled there after a visit to another tab; it used to come back at the
    /// top. Read off the grid's own scroll bar rather than the rows on screen, which the grid recycles.
    /// Settings → Accounts is not in here: Settings reloads the accounts on every visit, emptying the list
    /// and filling it again, and that brings the accounts grid back at the top.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Settings proxies")]
    [InlineData("Logs status")]
    public void Grids_KeepTheirScrollPosition_AcrossATabRoundTrip(string grid)
    {
        using Shell shell = new();
        MainViewModel vm = shell.Vm;
        int tab;
        DataGrid shown;
        object last;
        if (grid == "Settings proxies")
        {
            for (int i = 0; i < 100; i++)
            {
                vm.ConnectionManagerViewModel.Proxies.Add(new ProxySettingItem(
                    new ProxySettingDto { Host = $"host{i}.example", Port = 8080, Type = ProxyType.Http, Enabled = true }));
            }

            vm.SettingsViewModel.SelectedCategoryIndex = ConnectionCategory;
            tab = SettingsTab;
            shown = ((SettingsView)shell.ViewOn(tab)).ProxyGrid;
            last = vm.ConnectionManagerViewModel.Proxies[^1];
        }
        else
        {
            vm.LogsViewModel.AutoScroll = false;
            for (int i = 0; i < 100; i++)
            {
                vm.LogsViewModel.StatusLogs.Add(new LogEntryViewModel(new LogEvent { Message = $"line {i}" }));
            }

            tab = LogsTab;
            shown = ((LogsView)shell.ViewOn(tab)).StatusLogGrid;
            last = vm.LogsViewModel.StatusLogs[^1];
        }

        shown.ScrollIntoView(last, null);
        Dispatcher.UIThread.RunJobs();
        double scrolled = VerticalOffset(shown);
        Assert.True(scrolled > 0, "the grid should have scrolled down");

        shell.Select(OtherThan(tab));
        shell.Select(tab);

        Assert.Equal(scrolled, VerticalOffset(shown), 1);
    }

    /// <summary>
    /// Opening the window must be quiet. A Settings binding that looks for the Window through the logical
    /// tree finds it while it is still being built — App assigns the DataContext only after the
    /// constructor — and logs a "Value is null" binding error on every startup.
    /// </summary>
    [AvaloniaFact]
    public void OpeningTheWindow_LogsNoBindingErrors()
    {
        using BindingErrorSink sink = BindingErrorSink.Install();
        using Shell shell = new();

        Assert.Empty(sink.Errors);
    }

    /// <summary>
    /// History builds its grouped view the first time it is shown, not at startup. Its DataContext is in
    /// place from the moment the window opens, and a grouped view built then would make the startup history
    /// load — one added row at a time, each regrouped — cost time that grows faster than the history itself,
    /// on every launch, even for users who never open History.
    /// </summary>
    [AvaloniaFact]
    public void History_BuildsItsGroupedView_OnlyOnceShown()
    {
        using Shell shell = new(); // opens on Uploads
        UploadedView history = (UploadedView)shell.ViewAt(HistoryTab);

        Assert.Null(history.FilesGrid.ItemsSource);
        Assert.Equal(0, CollectionChangedSubscriberCount(shell.Vm.UploadedViewModel.Files));

        shell.Select(HistoryTab);

        Assert.IsType<DataGridCollectionView>(history.FilesGrid.ItemsSource);
    }

    /// <summary>
    /// The account selected in Settings → Accounts, and the column sort, survive a visit to another tab.
    /// Entering Settings reloads the accounts and re-selects the previous one by id. That reload completes
    /// synchronously, so run as the tab changes it would finish before the Settings view is back in the
    /// window: the detached grid would miss the reloaded rows and, once back, drop the re-selection.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsAccounts_KeepTheirSortAndSelectedAccount_AcrossATabRoundTrip()
    {
        using Shell shell = new();
        FileHosterLoginRepository logins = shell.Provider.GetRequiredService<FileHosterLoginRepository>();
        await logins.InsertAsync(new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "alice", IsAnonymous = false });
        await logins.InsertAsync(new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "bob", IsAnonymous = false });
        AccountManagerViewModel accounts = shell.Vm.SettingsViewModel.AccountManager;
        shell.Vm.SettingsViewModel.SelectedCategoryIndex = AccountsCategory;
        SettingsView settings = (SettingsView)shell.ViewOn(SettingsTab);
        Pump();
        settings.accountsGrid.SelectedItem = accounts.Accounts.Single(a => a.Username == "bob");
        IDataGridCollectionView view = settings.accountsGrid.CollectionView;
        view.SortDescriptions.Add(
            DataGridSortDescription.FromPath(nameof(FileHosterLoginDto.Username), ListSortDirection.Descending));
        Dispatcher.UIThread.RunJobs();
        int bob = accounts.SelectedAccount!.Id;

        shell.Select(UploadsTab);
        shell.Select(SettingsTab);
        Pump();

        Assert.Equal(bob, accounts.SelectedAccount?.Id);
        Assert.Same(accounts.SelectedAccount, settings.accountsGrid.SelectedItem);
        Assert.Same(view, settings.accountsGrid.CollectionView);
        Assert.Single(view.SortDescriptions);
        Assert.Equal(["bob", "alice"], view.Cast<FileHosterLoginDto>().Select(a => a.Username));
    }

    /// <summary>Lets the fire-and-forget account reload (an EF query) finish and land.</summary>
    private static void Pump()
    {
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static int OtherThan(int tab) => tab == UploadsTab ? HistoryTab : UploadsTab;

    private static double VerticalOffset(DataGrid grid)
        => grid.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Orientation.Vertical).Value;

    /// <summary>The real MainWindow over the real DI graph — the same recipe as <c>MainWindowMenuTests</c>,
    /// with the update service mocked so Velopack's locator stays out of the resolution path.</summary>
    private sealed class Shell : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"csu-tabs-{Guid.NewGuid():N}");
        private readonly ServiceProvider _provider;
        private readonly MainWindow _window;

        public Shell()
        {
            Directory.CreateDirectory(_tempDir);
            ServiceCollection services = new();
            App.ConfigureServices(services, _tempDir);
            services.AddSingleton<IUpdateService>(Mock.Of<IUpdateService>());
            _provider = services.BuildServiceProvider();
            using (CSUploaderDbContext db = _provider.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>().CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            Vm = _provider.GetRequiredService<MainViewModel>();
            _window = new MainWindow { DataContext = Vm };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public MainViewModel Vm { get; }

        public ServiceProvider Provider => _provider;

        /// <summary>
        /// Switches tabs through the bound SelectedTabIndex — view-model to TabControl, the path the app's own
        /// jumps take. It ends in the same selection change and content swap a tab-strip click makes, but
        /// does not exercise a click's write-back from the TabControl to the view-model.
        /// </summary>
        public void Select(int tab)
        {
            Vm.SelectedTabIndex = tab;
            Dispatcher.UIThread.RunJobs();
        }

        public Control ViewOn(int tab)
        {
            Select(tab);
            return ViewAt(tab);
        }

        /// <summary>A tab's view, without selecting the tab.</summary>
        public Control ViewAt(int tab)
        {
            TabControl tabs = _window.GetVisualDescendants().OfType<TabControl>().First();
            return (Control)((TabItem)tabs.Items[tab]!).Content!;
        }

        public void Dispose()
        {
            _window.Close();
            _provider.Dispose(); // stops the VMs' timers

            // The SQLite pool keeps the database file open after the provider is gone, which made the
            // delete below fail on every run and left a directory behind in %TEMP%.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
