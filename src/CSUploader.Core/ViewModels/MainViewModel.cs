// <copyright file="MainViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Update;
using CSUploader.Upload;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly IAppLogger _logger;
    private readonly IUpdateService _updateService;
    private readonly Services.IUpdateProgressSink _updateProgressSink;
    private readonly Services.IUiDispatcher _uiDispatcher;
    private readonly Services.IToastNotificationService _toastService;
    private readonly Services.IUiTimer _updateTimer;
    private readonly PropertyChangedEventHandler _localizerChanged;
    private UpdateAvailableInfo? _availableUpdate;
    private bool _backgroundCheckFailing;
    private bool _suppressDarkModePersist;
    private bool _initialized;
    private bool _disposed;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeMenuLabel))]
    public partial bool IsDarkMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string? AvailableVersion { get; set; }

    public string ThemeMenuLabel => IsDarkMode
        ? Localizer.Instance["Main_Menu_View_LightMode"]
        : Localizer.Instance["Main_Menu_View_DarkMode"];

    public string WindowTitle => IsUpdateAvailable
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_Title_UpdateAvailable_Format"], AvailableVersion)
        : Localizer.Instance["Main_Title"];

    [RelayCommand]
    private void ToggleTheme() => IsDarkMode = !IsDarkMode;

    public MainViewModel(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<IAppLogger>();
        _updateService = services.GetRequiredService<IUpdateService>();
        _updateProgressSink = services.GetRequiredService<Services.IUpdateProgressSink>();
        _uiDispatcher = services.GetRequiredService<Services.IUiDispatcher>();
        _toastService = services.GetRequiredService<Services.IToastNotificationService>();

        UploadsViewModel = services.GetRequiredService<UploadsViewModel>();
        UploadedViewModel = services.GetRequiredService<UploadedViewModel>();
        SettingsViewModel = services.GetRequiredService<SettingsViewModel>();
        ConnectionManagerViewModel = services.GetRequiredService<ConnectionManagerViewModel>();
        LogsViewModel = services.GetRequiredService<LogsViewModel>();

        _logger.OnLogOutput += Logger_OnLogOutput;

        // ThemeMenuLabel and WindowTitle read from Localizer; refresh them when culture
        // flips so the menu/title text updates live alongside the {loc:Loc} bindings.
        // Captured (not inline) so Dispose can detach the SAME delegate instance — Localizer.Instance
        // is a process-global static, so an un-detached handler leaks the VM for the whole process
        // lifetime (Phase 9 ledger fix c).
        _localizerChanged = (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeMenuLabel));
            OnPropertyChanged(nameof(WindowTitle));
        };
        Localizer.Instance.PropertyChanged += _localizerChanged;

        // CreateTimer yields an inert timer when no UI thread is running (e.g. unit tests),
        // so this stays a no-op there just as the old Application.Current guard did.
        // The tick discards the task (fire-and-forget) — harmonized with the startup check in
        // InitializeAsync; CheckForUpdatesAsync cannot return a faulted task (both its awaits —
        // the check and the dispatcher apply — are wrapped in try/catch).
        _updateTimer = _uiDispatcher.CreateTimer(UpdateCheckInterval, () => _ = CheckForUpdatesAsync());
        _updateTimer.Start();
    }

    /// <summary>
    /// Polls for a newer release. Safe to call from any thread; publishes onto the UI dispatcher.
    /// A background failure (<paramref name="userInitiated"/> == false) shows a debounced toast —
    /// once per failure episode, re-armed after the next successful check — so a chronically
    /// offline machine isn't nagged every poll. A user-initiated check shows nothing here; the
    /// caller renders the returned <see cref="UpdateCheckResult"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool userInitiated = false)
    {
        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: CheckAsync catches internally, but a poll tick must never fault.
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            result = UpdateCheckResult.Failed(ex.Message);
        }

        try
        {
            await _uiDispatcher.InvokeAsync(() => ApplyCheckResult(result, userInitiated));
        }
        catch (Exception ex)
        {
            // Keep the fire-and-forget timer tick fault-free: a throw while applying the result
            // (Localizer/toast) is logged rather than left as an unobserved faulted task.
            _logger.Log(this, LogType.Error, $"Applying update-check result failed: {ex.Message}");
        }

        return result;
    }

    private void ApplyCheckResult(UpdateCheckResult result, bool userInitiated)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                _availableUpdate = result.Info;
                IsUpdateAvailable = true;
                AvailableVersion = result.Info!.NewVersion;
                _backgroundCheckFailing = false;
                _logger.Log(this, LogType.Status, $"Update available: v{result.Info.NewVersion} (current v{_updateService.CurrentVersion})");
                break;

            case UpdateCheckStatus.UpToDate:
            case UpdateCheckStatus.NotInstalled:
                _availableUpdate = null;
                IsUpdateAvailable = false;
                AvailableVersion = null;
                _backgroundCheckFailing = false;
                break;

            case UpdateCheckStatus.Failed:
                // A transient failure must NOT hide a previously-known available update, so leave
                // IsUpdateAvailable/_availableUpdate as they are. Surface a background failure once
                // per episode; a user-initiated failure is rendered by the caller from the result.
                if (!userInitiated && !_backgroundCheckFailing)
                {
                    _backgroundCheckFailing = true;
                    _toastService.ShowInfo(
                        Localizer.Instance["Update_CheckFailed_ToastTitle"],
                        Localizer.Instance["Update_CheckFailed_ToastBody"]);
                }

                break;
        }
    }

    private bool CanInstallUpdate() => IsUpdateAvailable && _availableUpdate is not null;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null)
        {
            return;
        }

        _updateProgressSink.Open();

        Progress<int> progress = new(_updateProgressSink.Report);
        try
        {
            _updateProgressSink.SetStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["UpdateProgress_StatusDownloading_Format"], _availableUpdate.NewVersion));
            await _updateService.DownloadAsync(_availableUpdate, progress).ConfigureAwait(true);

            _updateProgressSink.SetStatus(Localizer.Instance["UpdateProgress_StatusRestarting"]);
            _updateService.ApplyAndRestart(_availableUpdate);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update install failed: {ex.Message}");
            _updateProgressSink.SetStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["UpdateProgress_StatusFailed_Format"], ex.Message));
        }
    }

    public void ActivateAndShowUploadedTab()
    {
        _services.GetService<Services.ITrayIconService>()?.ShowMainWindow();
        SelectedTabIndex = 1; // Uploaded tab (order: Uploads, Uploaded, Settings, Logs).
    }

    public UploadsViewModel UploadsViewModel { get; }

    public UploadedViewModel UploadedViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public ConnectionManagerViewModel ConnectionManagerViewModel { get; }

    public LogsViewModel LogsViewModel { get; }

    public async Task InitializeAsync()
    {
        // Idempotency guard (Phase 9 ledger fix b): InitializeAsync loads persisted packages, hydrates and
        // wires log persistence, and restores theme — none of it safe to run twice (it would double-load
        // packages and re-hydrate the Logs tab). The Avalonia head re-raises Window.Opened on every tray
        // restore (App.axaml.cs one-shots the outer call too); guarding here makes the VM safe for any
        // caller/head. The flag is set BEFORE the first await — FirstRun is synchronous, so on the single UI
        // thread a re-entrant call short-circuits before any body work. WPF's Loaded fires once: unchanged.
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        FirstRun.InitializeDatabase(_services, _logger);

        // Hydrate the Logs tab from the persisted store BEFORE wiring the persistence
        // handler, so this session's events aren't double-counted. Old entries keep
        // their original DateTime, which is the whole point of persistence.
        LogEntryRepository logEntryRepo = _services.GetRequiredService<LogEntryRepository>();
        try
        {
            // Best-effort retention: drop entries older than 30 days so the table doesn't
            // grow unbounded across long-running installs.
            await logEntryRepo.DeleteOlderThanAsync(DateTime.Now.AddDays(-30));

            LogEntryDto[] recent = await logEntryRepo.GetRecentAsync(5000);
            foreach (LogEntryDto entry in recent)
            {
                LogEvent ev = new()
                {
                    DateTime = entry.DateTime,
                    LogType = entry.LogType,
                    Filename = entry.Filename,
                    Function = entry.Function,
                    LineNumber = entry.LineNumber,
                    ThreadId = entry.ThreadId,
                    Message = entry.Message,
                };
                LogsViewModel.AddLogEntry(ev);
            }
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to load persisted log entries: {ex.Message}");
        }

        // Persist every Status/Error/UI entry going forward. HTTP entries carry an
        // HttpTransaction with bodies/headers we don't want to dump into SQLite, so
        // they stay session-only. Fire-and-forget — logging must never crash the app.
        _logger.OnLogOutput += (_, e) =>
        {
            if (e.LogType == LogType.Http)
            {
                return;
            }

            LogEntryDto dto = new()
            {
                DateTime = e.DateTime,
                LogType = e.LogType,
                Filename = e.Filename,
                Function = e.Function,
                LineNumber = e.LineNumber,
                ThreadId = e.ThreadId,
                Message = e.Message ?? string.Empty,
            };
            _ = Task.Run(async () =>
            {
                try
                {
                    await logEntryRepo.InsertAsync(dto);
                }
                catch
                {
                    // Swallow — a logging failure must not crash the app, and re-logging
                    // here would risk a feedback loop.
                }
            });
        };

        // Restore the persisted theme before the user sees the UI to avoid a light->dark
        // flash. Suppress the change handler's auto-save while we apply the loaded value.
        SettingRepository settingRepo = _services.GetRequiredService<SettingRepository>();
        SettingDto? darkSetting = await settingRepo.FindByKeyAsync(SettingKey.IsDarkMode);
        if (darkSetting is not null)
        {
            bool savedDark = string.Equals(darkSetting.Value, "true", StringComparison.OrdinalIgnoreCase);
            _suppressDarkModePersist = true;
            try
            {
                IsDarkMode = savedDark;
            }
            finally
            {
                _suppressDarkModePersist = false;
            }
        }

        await SettingsViewModel.LoadAsync();
        // Load proxies before persisted packages so any auto-resumed uploads pick from
        // the user's configured proxy list.
        await _services.GetRequiredService<Lib.Net.ProxyManager>().ReloadAsync();
        await ConnectionManagerViewModel.LoadAsync();
        await _services.GetRequiredService<PackageManager>().LoadPersistedPackagesAsync();
        await UploadedViewModel.LoadAsync();

        // First update check happens shortly after startup (the timer fires every 6h
        // thereafter). Fire-and-forget — a network failure shouldn't block init.
        _ = CheckForUpdatesAsync();
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        // No-op when no theme applier is registered (headless tests), exactly as the old
        // Application.Current-null guard did. The WPF applier also flips the immersive
        // dark title bar (see WpfThemeApplier.ApplyTheme).
        _services.GetService<Services.IThemeApplier>()?.ApplyTheme(value);

        if (_suppressDarkModePersist)
        {
            return;
        }

        // Fire-and-forget persist. The setting key is small and a failed save just
        // means we'll fall back to the default on next startup.
        _ = Task.Run(async () =>
        {
            try
            {
                SettingRepository repo = _services.GetRequiredService<SettingRepository>();
                SettingDto? existing = await repo.FindByKeyAsync(SettingKey.IsDarkMode);
                string newValue = value ? "true" : "false";
                if (existing is not null)
                {
                    existing.Value = newValue;
                    await repo.UpdateAsync(existing);
                }
                else
                {
                    await repo.InsertAsync(new SettingDto { Key = SettingKey.IsDarkMode, Value = newValue });
                }
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to persist dark mode preference: {ex.Message}");
            }
        });
    }

    private void Logger_OnLogOutput(object? sender, LogEvent e) => _uiDispatcher.Post(() => LogsViewModel.AddLogEntry(e));

    /// <summary>
    /// Stops the 6h update timer and detaches the process-global Localizer subscription and the logger
    /// handler. The singleton VM is disposed with the DI provider at app exit (both heads); tests that
    /// build a MainViewModel must dispose it, or the Localizer static accumulates dead subscribers across
    /// the run. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);

        _updateTimer.Stop();
        Localizer.Instance.PropertyChanged -= _localizerChanged;
        _logger.OnLogOutput -= Logger_OnLogOutput;

        // The InitializeAsync log-persistence handler (:~215, an inline lambda wired only once
        // InitializeAsync runs) is intentionally NOT detached: it captures the LogEntryRepository that is
        // disposed with the DI provider, and detaching it is out of this fix's design scope (ledger fix c).
    }
}
