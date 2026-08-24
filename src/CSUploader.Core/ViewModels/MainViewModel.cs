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
    private readonly Lock _checkGate = new();
    private Task<UpdateCheckResult>? _inFlightCheck;
    private UpdateCheckOrigin _inFlightOrigin;

    private UpdateAvailableInfo? _availableUpdate;
    private bool _backgroundCheckFailing;
    private bool _suppressDarkModePersist;
    private readonly Lock _initializeGate = new();
    private Task? _initializeTask;
    private bool _disposed;

    /// <summary>Tab order is Uploads, Uploaded, Settings, Logs — so Uploads is 0 and Uploaded is 1.</summary>
    private const int UploadsTabIndex = 0;
    private const int UploadedTabIndex = 1;
    private const int SettingsTabIndex = 2;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    // Visibility-gate the per-tab refresh work: the Uploads VM skips its 500 ms tick and the Uploaded VM
    // defers its completion-driven full reloads while their tab isn't showing (nothing either refreshes
    // is visible then). Both VMs are assigned in the constructor before any tab change can fire this.
    partial void OnSelectedTabIndexChanged(int value)
    {
        UploadsViewModel.SetActive(value == UploadsTabIndex);
        UploadedViewModel.SetActive(value == UploadedTabIndex);

        // Re-read the accounts on the way in. The account manager is a singleton whose list is
        // filled once at startup and otherwise only when Settings itself edits one — so an account
        // added from the upload wizard (which writes straight to the repository) was invisible here
        // until the app restarted. Reloading on show fixes that for ANY writer, not just the wizard,
        // and costs one small read of a table with a handful of rows. LoadAccountsAsync re-selects
        // the previous row by id, so nothing the user had highlighted is lost.
        if (value == SettingsTabIndex)
        {
            SettingsViewModel.AccountManager.ReloadAccountsAsync();
        }
    }

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

        // Sync the per-tab visibility gates to the STARTUP tab: the app opens on Uploads (index 0) and no
        // OnSelectedTabIndexChanged fires for the initial value, so without this the Uploaded VM would
        // stay in its default-active state and keep full-reloading on every completion burst even though
        // its tab has never been shown. (Startup history population is unaffected — InitializeAsync calls
        // UploadedViewModel.LoadAsync() directly, bypassing the gate.)
        UploadsViewModel.SetActive(SelectedTabIndex == UploadsTabIndex);
        UploadedViewModel.SetActive(SelectedTabIndex == UploadedTabIndex);

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
        _updateTimer = _uiDispatcher.CreateTimer(UpdateCheckInterval, () => _ = CheckForUpdatesAsync(UpdateCheckOrigin.Periodic));
        _updateTimer.Start();
    }

    /// <summary>
    /// Runs an update check and publishes its result. This is the ONLY place update state is
    /// written, so a startup check, the six-hourly poll and a user pressing Check for Updates cannot
    /// each own a different idea of what version is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single flight.</b> A caller arriving while a check is already running JOINS it rather than
    /// queueing behind it. That is what stops the startup check - which outlives its own deadline,
    /// because Velopack 1.2.0 offers no way to cancel one - from blocking a user who presses Check
    /// for Updates a moment later. They get the in-flight answer, which is as current as the one a
    /// second network round trip would have produced, and the app makes one call instead of two.
    /// </para>
    /// <para>
    /// It also removes the ordering problem rather than managing it: only one check publishes at a
    /// time and the next cannot start until it has, so no result can overwrite a newer one and no
    /// generation stamp is needed to prevent it.
    /// </para>
    /// <para>
    /// A joiner does NOT change how the running check reports failure - the originator's
    /// <see cref="UpdateCheckOrigin"/> governs that. A user joining a silent startup check still
    /// sees the outcome, because the menu handler renders the returned result itself.
    /// </para>
    /// </remarks>
    public Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateCheckOrigin origin)
    {
        TaskCompletionSource<UpdateCheckResult> started;
        lock (_checkGate)
        {
            if (_inFlightCheck is { IsCompleted: false })
            {
                // A joiner can make the running check LOUDER but never quieter - see
                // EffectiveOrigin. Upgraded under the lock, read under the lock at publication.
                _inFlightOrigin = MoreVisible(_inFlightOrigin, origin);
                return _inFlightCheck;
            }

            // Published before RunCheckAsync can run a line, for the same reason InitializeAsync
            // publishes before FirstRun: the body reaches collaborator code - ApplyCheckResult logs
            // synchronously for an available update - and a log subscriber calling back in here
            // would otherwise find this null and start a second check. Assigning the result of
            // RunCheckAsync(...) cannot do that, because the assignment happens only once the method
            // has already returned, and a synchronously-completing check returns after logging.
            started = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightCheck = started.Task;
            _inFlightOrigin = origin;
        }

        _ = CompleteCheckAsync(started);
        return started.Task;
    }

    private async Task CompleteCheckAsync(TaskCompletionSource<UpdateCheckResult> started)
    {
        try
        {
            started.TrySetResult(await RunCheckAsync());
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
        }
    }

    /// <summary>
    /// Which of two origins reports more visibly. A check that several callers share must not be
    /// quieter than the loudest of them asked for: a user who joins a silent startup check still
    /// gets their dialog, and a periodic poll joining one still gets its toast.
    /// </summary>
    private static UpdateCheckOrigin MoreVisible(UpdateCheckOrigin a, UpdateCheckOrigin b)
        => (UpdateCheckOrigin)Math.Max((int)a, (int)b);

    private async Task<UpdateCheckResult> RunCheckAsync()
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

        UpdateCheckOrigin origin;
        lock (_checkGate)
        {
            // Read under the lock so a joiner that arrived while the network call was running is
            // accounted for, and one arriving after this point cannot change what was published.
            origin = _inFlightOrigin;
        }

        try
        {
            await _uiDispatcher.InvokeAsync(() => ApplyCheckResult(result, origin));
        }
        catch (Exception ex)
        {
            // Keep the fire-and-forget timer tick fault-free: a throw while applying the result
            // (Localizer/toast) is logged rather than left as an unobserved faulted task.
            _logger.Log(this, LogType.Error, $"Applying update-check result failed: {ex.Message}");
        }

        return result;
    }

    private void ApplyCheckResult(UpdateCheckResult result, UpdateCheckOrigin origin)
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
                // per episode; a user-initiated failure is rendered by the caller from the result,
                // and a STARTUP failure is silent - the splash is on screen and the main window does
                // not exist yet, so a toast would be orphaned or hidden behind it.
                if (origin == UpdateCheckOrigin.Periodic && !_backgroundCheckFailing)
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

        // The updater reports a percentage and nothing else, so the bytes, the rate and the time
        // remaining are all derived here. Progress<T> captures this thread's synchronization
        // context, which is the UI thread's, so every Report lands back on it - which is both what
        // the sink requires and what lets UpdateDownloadStats stay free of locking.
        UpdateDownloadStats stats = new(_availableUpdate.DownloadPlan);
        Progress<int> progress = new(percent => _updateProgressSink.Report(stats.Report(percent)));
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

    /// <summary>
    /// Runs startup hydration exactly once, and returns the SAME task to every caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It loads persisted packages, hydrates and wires log persistence, and restores the theme —
    /// none of it safe to run twice — and the Avalonia head re-raises <c>Window.Opened</c> on every
    /// tray restore, so it needs a guard.
    /// </para>
    /// <para>
    /// The guard is a cached TASK rather than a bool. A bool made a second caller return
    /// IMMEDIATELY while the first was still working, so it reported "initialised" for a database
    /// that was not loaded yet — harmless while only one caller existed, and not harmless now that
    /// the splash starts this and <c>MainWindow.Opened</c> also awaits it. Every caller now awaits
    /// the same work and observes the same fault.
    /// </para>
    /// </remarks>
    public Task InitializeAsync()
    {
        TaskCompletionSource started;
        lock (_initializeGate)
        {
            if (_initializeTask is not null)
            {
                return _initializeTask;
            }

            // Published BEFORE any work begins, not after InitializeCoreAsync returns its task. An
            // async method runs synchronously to its first incomplete await, and the first thing
            // this one does is FirstRun.InitializeDatabase - which LOGS, synchronously, to
            // subscribers that run synchronously. A subscriber calling back in here would have
            // found the field still null and started a second initialisation. The lock is no help
            // against that: it is re-entrant on the calling thread.
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializeTask = started.Task;
        }

        _ = CompleteInitializeAsync(started);
        return started.Task;
    }

    private async Task CompleteInitializeAsync(TaskCompletionSource started)
    {
        try
        {
            await InitializeCoreAsync();
            started.TrySetResult();
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
        }
    }

    private async Task InitializeCoreAsync()
    {
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

        // Restore the persisted theme onto the VM property the menu binds to. This does NOT prevent a
        // startup flash and never could: it runs from MainWindow.Opened, i.e. once the window is
        // already on screen, and behind the database init and log hydration above. The head applies
        // the same setting before the first window is built (StartupTheme.ReadPersistedDarkMode), so
        // by the time this assigns, it is re-applying a value that is already on screen.
        // Suppress the change handler's auto-save while we apply the loaded value.
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
        _ = CheckForUpdatesAsync(UpdateCheckOrigin.Startup);
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
