// <copyright file="MainWindowMenuTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib.Update;
using CSUploader.Services;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless structure/binding tests for the Phase 7 Task 5 File/View/Help menu bar on
/// <see cref="MainWindow"/> (port of the WPF menu, rule 41). Covers: the three top-level menus and the
/// four tabs survive the Grid restructure; the WPF sub-item shape (checkable Upload-Overview via
/// <see cref="MenuItemToggleType.CheckBox"/>, the two Separators); the checkable item's TWO-way
/// <c>IsChecked</c> binding to <c>UploadsViewModel.ShowUploadOverview</c> and the View/Help command
/// bindings; the menu Exit handler closing the window (the Task 5 staged <c>_forceClose</c> + <c>Close()</c>
/// path — Task 6 upgrades it with the close-to-tray guard); and the Check-for-updates guard.
/// Every shown window is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class MainWindowMenuTests
{
    [AvaloniaFact]
    public void MainWindow_HasFileViewHelpMenu_AndFourTabs()
    {
        // DataContext left null: {loc:Loc} headers resolve without one; the {Binding} items still exist.
        var w = new MainWindow();
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu? menu = w.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
            Assert.NotNull(menu);
            Assert.Equal(3, menu!.Items.Count); // File / View / Help

            TabControl? tabs = w.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            Assert.NotNull(tabs);
            Assert.Equal(4, tabs!.Items.Count); // Uploads / Uploaded / Settings / Logs (the restructure kept them)
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void Menu_SubItemStructure_MatchesWpf_WithCheckableOverviewAndSeparators()
    {
        var w = new MainWindow();
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu menu = w.GetVisualDescendants().OfType<Menu>().First();

            // File → [Exit]
            var file = (MenuItem)menu.Items[0]!;
            Assert.Single(file.Items);
            Assert.IsType<MenuItem>(file.Items[0]);

            // View → [Upload-Overview (checkable), Separator, theme toggle]
            var view = (MenuItem)menu.Items[1]!;
            Assert.Equal(3, view.Items.Count);
            var overview = (MenuItem)view.Items[0]!;
            Assert.Equal(MenuItemToggleType.CheckBox, overview.ToggleType); // rule 41: IsCheckable → ToggleType=CheckBox
            Assert.IsType<Separator>(view.Items[1]);
            Assert.IsType<MenuItem>(view.Items[2]); // theme toggle (Header/Command bound — asserted below)

            // Help → [Check for updates, Install update, Separator, About]
            var help = (MenuItem)menu.Items[2]!;
            Assert.Equal(4, help.Items.Count);
            Assert.IsType<MenuItem>(help.Items[0]);
            Assert.IsType<MenuItem>(help.Items[1]);
            Assert.IsType<Separator>(help.Items[2]);
            Assert.IsType<MenuItem>(help.Items[3]);

            // The only checkable item in the whole menu is the Upload-Overview toggle.
            Assert.Single(AllMenuItems(menu), m => m.ToggleType == MenuItemToggleType.CheckBox);
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void UploadOverviewMenuItem_TwoWayBinds_And_View_Help_CommandsBound()
    {
        // A real MainViewModel from the real graph, not a shape-alike: the menu binds COMPILED now
        // (MainWindow x:DataType), so a reflection stand-in with look-alike members no longer binds at
        // all. Same DI recipe as MenuCheckForUpdates_WhenCheckFails_ShowsErrorDialog below; the mocked
        // IUpdateService keeps Velopack's locator out of the resolution path.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-menu-overview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ServiceCollection services = new();
        App.ConfigureServices(services, tempDir);
        services.AddSingleton<IUpdateService>(Mock.Of<IUpdateService>()); // last registration wins for GetRequiredService
        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            using (CSUploaderDbContext db = provider.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>().CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            var w = new MainWindow { DataContext = vm };
            try
            {
                w.Show();
                Dispatcher.UIThread.RunJobs();

                Menu menu = w.GetVisualDescendants().OfType<Menu>().First();
                var view = (MenuItem)menu.Items[1]!;
                var overview = (MenuItem)view.Items[0]!;
                var theme = (MenuItem)view.Items[2]!;
                var help = (MenuItem)menu.Items[2]!;
                var install = (MenuItem)help.Items[1]!;

                // VM → UI: the menu check mirrors the VM property through both flips.
                vm.UploadsViewModel.ShowUploadOverview = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(overview.IsChecked);

                vm.UploadsViewModel.ShowUploadOverview = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(overview.IsChecked);

                // UI → VM (the two-way half): checking the menu item writes back to the VM.
                overview.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.UploadsViewModel.ShowUploadOverview);

                // The View theme toggle and Help Install-update items bind their Command to the VM's commands.
                Assert.Same(vm.ToggleThemeCommand, theme.Command);
                Assert.Same(vm.InstallUpdateCommand, install.Command);
            }
            finally
            {
                w.Close();
            }
        }
        finally
        {
            provider.Dispose(); // stops the VM's timers + detaches its Localizer subscription
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort; Windows may still hold a handle on the SQLite file */ }
        }
    }

    [AvaloniaFact]
    public void MenuExit_Click_ClosesWindow()
    {
        // Task 5 staging: MenuExit_Click sets _forceClose = true and calls Close(); with no Closing handler
        // yet (Task 6 adds the close-to-tray reroute the flag bypasses), Close() closes the window outright.
        var w = new MainWindow();
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu menu = w.GetVisualDescendants().OfType<Menu>().First();
            var file = (MenuItem)menu.Items[0]!;
            var exit = (MenuItem)file.Items[0]!;

            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible); // closed, not merely hidden
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void MenuCheckForUpdates_WithoutMainViewModel_NoOps()
    {
        // The handler guards on `DataContext is MainViewModel`; a non-MainViewModel context must no-op —
        // no throw from the async void handler, no message box, window stays open.
        var w = new MainWindow { DataContext = new object() };
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu menu = w.GetVisualDescendants().OfType<Menu>().First();
            var help = (MenuItem)menu.Items[2]!;
            var checkForUpdates = (MenuItem)help.Items[0]!;

            checkForUpdates.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(w.IsVisible); // guard prevented any close/dialog
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task MenuCheckForUpdates_WhenCheckFails_ShowsErrorDialog()
    {
        // A user-initiated check that FAILS must surface an error dialog (not the "you're on the latest"
        // info box). Drives the real handler over a real MainViewModel whose IUpdateService returns Failed
        // (the AvaloniaStartupDISmokeTests/UploadsViewTests DI pattern), then asserts the owned
        // MessageBoxWindow is the Error shape — i.e. the handler took the Failed -> ShowErrorAsync branch.
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-menu-updatefail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        ServiceCollection services = new();
        App.ConfigureServices(services, tempDir);
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("network down"));
        services.AddSingleton<IUpdateService>(updater.Object); // last registration wins for GetRequiredService
        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            using (CSUploaderDbContext db = provider.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>().CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            MainViewModel vm = provider.GetRequiredService<MainViewModel>();
            var w = new MainWindow { DataContext = vm };
            try
            {
                w.Show();
                Dispatcher.UIThread.RunJobs();

                Menu menu = w.GetVisualDescendants().OfType<Menu>().First();
                var help = (MenuItem)menu.Items[2]!;
                var checkForUpdates = (MenuItem)help.Items[0]!;
                checkForUpdates.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                // The click's first visible effect is now the manual-check splash — the whole point
                // of the choreography (an invisible await read as the menu item doing nothing).
                Assert.NotNull(w.OwnedWindows.OfType<SplashWindow>().FirstOrDefault());

                // The handler is async void, and the splash holds the answer back for its real-time
                // display floor — so pump with real delays until the owned dialog appears (bounded).
                MessageBoxWindow? box = null;
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (box is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                    Dispatcher.UIThread.RunJobs();
                    box = w.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault();
                }

                Assert.NotNull(box);
                Assert.Equal(MessageBoxIcon.Error, box!.IconKind); // Failed rendered the error box, not the info box
                Assert.Empty(w.OwnedWindows.OfType<SplashWindow>()); // and the splash is gone once the answer shows

                box.Close();
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                w.Close();
            }
        }
        finally
        {
            provider.Dispose(); // stops the VM's 6h timer + detaches its Localizer subscription
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort; Windows may still hold a handle on the SQLite file */ }
        }
    }

    private static IEnumerable<MenuItem> AllMenuItems(Menu menu)
        => menu.Items.OfType<MenuItem>()
            .SelectMany(top => new[] { top }.Concat(top.Items.OfType<MenuItem>()));
}
