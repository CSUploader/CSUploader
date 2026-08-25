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
using CSUploader.Lib.Update;
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
        // OS/app shutdown must NOT be rerouted to the tray. Unlike WPF — where session end surfaces only via
        // the (unsubscribed) Application.SessionEnding and never through Window.Closing — Avalonia routes
        // WM_QUERYENDSESSION through Window.Closing: Win32Platform's WndProc (msg 17) raises ShutdownRequested,
        // which calls DoShutdown, and the cancel is HONORED for the MainWindow under ShutdownMode.OnMainWindowClose
        // (OnMainWindowClose). desktop.Shutdown() arrives the same way. So rerouting here (e.Cancel = true +
        // Hide/tray side effects) would VETO Windows shutdown/logoff and, with CloseAction.Ask, pop a stray
        // dialog mid-logoff. Returning early — before the _forceClose/_settings check — restores WPF parity.
        // On Windows (Avalonia 11.3.18) the OS-session-end path arrives as ApplicationShutdown (OSShutdown is
        // unused by Avalonia.Win32); both are guarded so the behaviour holds on other platforms/versions too.
        // User X-click and programmatic Close() both arrive as WindowCloseReason.WindowClosing, so the reroute
        // below still fires for them.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

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
        try
        {
            CloseActionChoice? choice = await new CloseActionDialog().ShowDialog<CloseActionChoice?>(this);
            await ApplyCloseActionChoiceAsync(choice);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from MainWindow_Closing (rule 44), so an escaping exception would otherwise
            // reach the dispatcher loop unobserved. Best-effort log, mirroring PersistCloseActionAsync below.
            Logger.Current.Log(this, LogType.Error, $"Close-action prompt failed: {ex.Message}");
        }
    }

    // Test seam (InternalsVisibleTo -> CSUploader.Tests): the post-dialog close-action decision,
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
            // Parity with the WPF Ask->Minimize branch: hide + force the icon for the session, NO first-hide
            // balloon here. EnsureIconForSession (not UpdateVisibility) because Remember=false leaves CloseAction
            // at Ask, so a settings-gated refresh would tear the icon down and strand the app hidden with no icon
            // (Phase 9 ledger fix a). Does NOT mutate in-memory CloseAction.
            Hide();
            _tray?.EnsureIconForSession();
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

    /// <summary>How long Help → Check for Updates waits for an answer before saying it is still
    /// working. Generous for a single HTTP round trip, short enough not to read as a hang.</summary>
    private static readonly TimeSpan ManualCheckPatience = TimeSpan.FromSeconds(20);

    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // Bounded, because checks are single flight: pressing this while the startup check is
            // still running JOINS that check rather than starting a second one, and the startup
            // check has no cancellation - Velopack 1.2.0 offers none - so it can outlive its own
            // deadline by a long way. Joining is still the right model (one network call, one
            // answer); what must not happen is this menu item hanging for the remainder of a request
            // the user never asked for. The check keeps running and still publishes its result.
            UpdateCheckResult result;
            try
            {
                result = await vm.CheckForUpdatesAsync(UpdateCheckOrigin.User).WaitAsync(ManualCheckPatience);
            }
            catch (TimeoutException)
            {
                result = UpdateCheckResult.Failed(Localizer.Instance["Main_CheckForUpdates_StillRunning"]);
            }

            string title = Localizer.Instance["Main_CheckForUpdates_DialogTitle"];

            // A switch EXPRESSION, not the statement this used to be, and the difference is the
            // point. The old form ended in `default:` = "you're on the latest version", so every
            // status it had not been taught about was answered with the one sentence that is worst
            // if wrong - AvailableNotInstallable means a newer release exists, and would have been
            // reported as being up to date. An expression with no discard arm makes the next status
            // a build failure here instead (CS8509).
            //
            // CS8524 is the other half of that diagnostic - a value cast from an int that matches no
            // member - and is suppressed rather than answered. Adding a `_` arm to satisfy it would
            // also satisfy CS8509, which is the one being relied on; the guard would be gone and the
            // silent-wrong-answer would be back.
#pragma warning disable CS8524
            (bool isError, string body) = result.Status switch
            {
                UpdateCheckStatus.Available => (
                    false,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_CheckForUpdates_Available_Format"], result.Info!.NewVersion)),
                UpdateCheckStatus.AvailableNotInstallable => (
                    false,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_CheckForUpdates_NotInstallable_Format"], result.NewVersion)),
                UpdateCheckStatus.Failed => (
                    true,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_CheckForUpdates_Failed_Format"], result.FailureReason)),
                UpdateCheckStatus.UpToDate => (false, Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"]),
            };
#pragma warning restore CS8524

            if (isError)
            {
                await MessageBoxWindow.ShowErrorAsync(this, body, title);
            }
            else
            {
                await MessageBoxWindow.ShowInformationAsync(this, body, title);
            }
        }
    }

    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
