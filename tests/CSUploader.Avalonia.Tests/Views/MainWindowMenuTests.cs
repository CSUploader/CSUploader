// <copyright file="MainWindowMenuTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Views;

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
        var fake = new FakeMainVm();
        var w = new MainWindow { DataContext = fake };
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

            // Initial state flows VM → UI (fake starts true).
            Assert.True(overview.IsChecked);

            // VM → UI: flipping the VM property updates the menu check.
            fake.UploadsViewModel.ShowUploadOverview = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(overview.IsChecked);

            // UI → VM (the two-way half): checking the menu item writes back to the VM.
            overview.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fake.UploadsViewModel.ShowUploadOverview);

            // The View theme toggle and Help Install-update items bind their Command to the VM's commands.
            Assert.Same(fake.ToggleThemeCommand, theme.Command);
            Assert.Same(fake.InstallUpdateCommand, install.Command);
        }
        finally
        {
            w.Close();
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

    private static IEnumerable<MenuItem> AllMenuItems(Menu menu)
        => menu.Items.OfType<MenuItem>()
            .SelectMany(top => new[] { top }.Concat(top.Items.OfType<MenuItem>()));

    // Reflection-bound stand-in (the head runs AvaloniaUseCompiledBindingsByDefault=false, so the menu's
    // {Binding} paths resolve by name). Exposes the members this test asserts against; the untested
    // Header/Title bindings (ThemeMenuLabel, WindowTitle) simply resolve to null on this partial double.
    private sealed class FakeMainVm
    {
        public FakeUploadsVm UploadsViewModel { get; } = new();

        public RelayCommand ToggleThemeCommand { get; } = new(() => { });

        public RelayCommand InstallUpdateCommand { get; } = new(() => { });

        public bool IsUpdateAvailable { get; set; } = true;

        public int SelectedTabIndex { get; set; }
    }

    private sealed class FakeUploadsVm : INotifyPropertyChanged
    {
        private bool showUploadOverview = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool ShowUploadOverview
        {
            get => this.showUploadOverview;
            set
            {
                if (this.showUploadOverview != value)
                {
                    this.showUploadOverview = value;
                    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowUploadOverview)));
                }
            }
        }
    }
}
