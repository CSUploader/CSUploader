// <copyright file="MainWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AppSettings _appSettings;
    private readonly TrayIconManager _trayIconManager;

    // Set to true when the user (or auto-update) really wants to quit. Bypasses the
    // close-to-tray rerouting in OnClosing so we don't trap shutdown.
    private bool _forceClose;

    public MainWindow(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _appSettings = _serviceProvider.GetRequiredService<AppSettings>();
        _trayIconManager = _serviceProvider.GetRequiredService<TrayIconManager>();

        InitializeComponent();

        DataContext = _serviceProvider.GetRequiredService<MainViewModel>();

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }

        // InitializeAsync hydrates AppSettings from the DB; sync the tray icon to it now.
        _trayIconManager.UpdateVisibility();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _appSettings.MinimizeToTray)
        {
            Hide();
            _trayIconManager.UpdateVisibility();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        switch (_appSettings.CloseAction)
        {
            case CloseAction.Exit:
                return;

            case CloseAction.MinimizeToTray:
                e.Cancel = true;
                Hide();
                _trayIconManager.UpdateVisibility();
                _trayIconManager.NotifyHidden();
                return;

            case CloseAction.Ask:
            default:
                CloseActionDialog dialog = new() { Owner = this };
                bool? result = dialog.ShowDialog();
                if (result != true)
                {
                    e.Cancel = true;
                    return;
                }

                if (dialog.Remember)
                {
                    _appSettings.CloseAction = dialog.ChosenAction;
                    _ = PersistCloseActionAsync(dialog.ChosenAction);
                }

                if (dialog.ChosenAction == CloseAction.MinimizeToTray)
                {
                    e.Cancel = true;
                    Hide();
                    _trayIconManager.UpdateVisibility();
                }

                // Else: ChosenAction == Exit, fall through to close. Even if !Remember,
                // honouring the just-made choice once is the obvious behaviour.
                break;
        }
    }

    private async Task PersistCloseActionAsync(CloseAction chosen)
    {
        try
        {
            Dal.SettingRepository repo = _serviceProvider.GetRequiredService<Dal.SettingRepository>();
            string value = chosen.ToString();
            Dal.SettingDto? existing = await repo.FindByKeyAsync(SettingKey.CloseAction);
            if (existing is null)
            {
                await repo.InsertAsync(new Dal.SettingDto { Key = SettingKey.CloseAction, Value = value });
            }
            else
            {
                existing.Value = value;
                await repo.UpdateAsync(existing);
            }
        }
        catch (Exception ex)
        {
            // Logged but not surfaced — the in-memory AppSettings is already updated, so
            // the choice still applies for the rest of this session.
            CSUploader.Lib.Logger.Current.Log(this, CSUploader.Lib.LogType.Error, $"Failed to persist close action: {ex.Message}");
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    /// <summary>
    /// Intercepts tab-header clicks before the TabControl commits the selection. If the
    /// user is leaving Settings with unsaved edits and declines the discard prompt, we
    /// swallow the click here — that avoids the brief flash of the new tab that the
    /// after-the-fact ViewModel revert produced. Keyboard navigation still goes through
    /// the ViewModel handler as a backstop.
    /// </summary>
    private void TabControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabControl tabControl)
        {
            return;
        }

        TabItem? targetTab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (targetTab is null || targetTab.IsSelected)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        int currentIndex = tabControl.SelectedIndex;
        int targetIndex = tabControl.ItemContainerGenerator.IndexFromContainer(targetTab);
        if (currentIndex != 2 || targetIndex == currentIndex)
        {
            // 2 == Settings tab. Only intercept when leaving it.
            return;
        }

        // Always swallow the click and route the navigation through the ViewModel — the
        // modal dialog inside TryConfirmDiscardChanges disrupts the TabControl's bubble
        // path enough that letting the original click through doesn't reliably commit
        // the tab change after the user confirms.
        e.Handled = true;
        if (vm.SettingsViewModel.TryConfirmDiscardChanges())
        {
            vm.SelectedTabIndex = targetIndex;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void MenuCheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.CheckForUpdatesAsync();
            MessageBox.Show(
                vm.IsUpdateAvailable
                    ? $"Update available: v{vm.AvailableVersion}.\n\nUse Help → Install Update to download and install."
                    : "You're on the latest version.",
                "Check for Updates",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }
}
