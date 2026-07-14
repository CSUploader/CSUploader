// <copyright file="MainWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// The shell window. Hosts the File/View/Help menu (Task 5) and the close/minimize-to-tray behaviour
/// (Task 6, port of the WPF <c>MainWindow</c>, rules 43 + 44): a <see cref="AppSettings.CloseAction"/> of
/// Exit / MinimizeToTray / Ask, an async close-action prompt (Avalonia's <c>Closing</c> cannot await
/// <c>ShowDialog</c>), and a WindowState watch that hides to the tray on minimize. The parameterless ctor
/// stays for the XAML loader and the menu/close tests (it wires no <c>Closing</c> reroute); the production
/// ctor takes the services the tray behaviour needs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings? _settings;
    private readonly ITrayIconService? _tray;
    private readonly SettingRepository? _settingRepo;

    // Set when the user (menu Exit) or the close-to-tray Exit choice really wants to quit, bypassing the
    // close-to-tray rerouting in MainWindow_Closing. Mirrors the WPF MainWindow._forceClose.
    private bool _forceClose;

    // Loader/design-time ctor (AVLN3001); the menu/close tests use this too (DataContext null, no Closing reroute).
    public MainWindow()
    {
        InitializeComponent();
    }

    // Production ctor: App.axaml.cs supplies the services close/minimize-to-tray needs.
    internal MainWindow(AppSettings settings, ITrayIconService tray, SettingRepository settingRepo)
    {
        _settings = settings;
        _tray = tray;
        _settingRepo = settingRepo;
        InitializeComponent();

        Closing += MainWindow_Closing;
    }

    // Avalonia has no StateChanged event (rule 43): react to WindowState via OnPropertyChanged.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty
            && WindowState == WindowState.Minimized
            && _settings is { MinimizeToTray: true })
        {
            // Direct minimize -> tray: hide + refresh the icon. NO balloon here — parity with the WPF
            // StateChanged handler, which shows none (NotifyHidden fires only on the MinimizeToTray Closing branch).
            Hide();
            _tray?.UpdateVisibility();
        }
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || _settings is null)
        {
            return;
        }

        switch (_settings.CloseAction)
        {
            case CloseAction.Exit:
                return;

            case CloseAction.MinimizeToTray:
                // Direct close -> tray: hide + refresh + the first-hide balloon. This is the ONLY hide path
                // that fires NotifyHidden (parity with the WPF MinimizeToTray Closing branch).
                e.Cancel = true;
                Hide();
                _tray?.UpdateVisibility();
                _tray?.NotifyHidden();
                return;

            case CloseAction.Ask:
            default:
                // Rule 44: Closing can't await ShowDialog. Cancel, prompt async, re-close on Exit.
                e.Cancel = true;
                _ = PromptCloseActionAsync();
                return;
        }
    }

    private async Task PromptCloseActionAsync()
    {
        CloseActionChoice? choice = await new CloseActionDialog().ShowDialog<CloseActionChoice?>(this);
        await ApplyCloseActionChoiceAsync(choice);
    }

    // Test seam (InternalsVisibleTo -> CSUploader.Avalonia.Tests): the post-dialog close-action decision,
    // factored out of PromptCloseActionAsync so the Ask outcomes (persist-on-remember, hide-on-minimize,
    // forceClose+Close-on-exit) are drivable headlessly — Avalonia.Headless can't click a modal ShowDialog.
    // Behaviour is identical to inlining it after the dialog await; matches the codebase's seam pattern
    // (AvaloniaDialogService / MessageBoxWindow / ToastWindow).
    internal async Task ApplyCloseActionChoiceAsync(CloseActionChoice? choice)
    {
        if (choice is not { } result)
        {
            return; // cancelled — keep the window open, setting unchanged.
        }

        if (result.Remember && _settings is not null)
        {
            _settings.CloseAction = result.Action;
            await PersistCloseActionAsync(result.Action);
        }

        if (result.Action == CloseAction.MinimizeToTray)
        {
            // Parity with the WPF Ask->Minimize branch: hide + refresh, NO first-hide balloon here.
            Hide();
            _tray?.UpdateVisibility();
            return;
        }

        // Exit: bypass the reroute and really close.
        _forceClose = true;
        Close();
    }

    private async Task PersistCloseActionAsync(CloseAction chosen)
    {
        if (_settingRepo is null)
        {
            return;
        }

        try
        {
            string value = chosen.ToString();
            SettingDto? existing = await _settingRepo.FindByKeyAsync(SettingKey.CloseAction);
            if (existing is null)
            {
                await _settingRepo.InsertAsync(new SettingDto { Key = SettingKey.CloseAction, Value = value });
            }
            else
            {
                existing.Value = value;
                await _settingRepo.UpdateAsync(existing);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the in-memory AppSettings already updated, so the choice applies this session.
            Logger.Current.Log(this, LogType.Error, $"Failed to persist close action: {ex.Message}");
        }
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.CheckForUpdatesAsync();
            string message = vm.IsUpdateAvailable
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Instance["Main_CheckForUpdates_Available_Format"], vm.AvailableVersion)
                : Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"];
            await MessageBoxWindow.ShowErrorAsync(this, message, Localizer.Instance["Main_CheckForUpdates_DialogTitle"]);
        }
    }

    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
