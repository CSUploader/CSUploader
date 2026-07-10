// <copyright file="MainViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Update;
using CSUploader.Upload;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly IAppLogger _logger;
    private readonly IUpdateService _updateService;
    private readonly Services.IUpdateProgressSink _updateProgressSink;
    private readonly DispatcherTimer? _updateTimer;
    private UpdateAvailableInfo? _availableUpdate;
    private bool _suppressDarkModePersist;

    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeMenuLabel))]
    private bool isDarkMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? availableVersion;

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

        UploadsViewModel = services.GetRequiredService<UploadsViewModel>();
        UploadedViewModel = services.GetRequiredService<UploadedViewModel>();
        SettingsViewModel = services.GetRequiredService<SettingsViewModel>();
        ConnectionManagerViewModel = services.GetRequiredService<ConnectionManagerViewModel>();
        LogsViewModel = services.GetRequiredService<LogsViewModel>();

        _logger.OnLogOutput += Logger_OnLogOutput;

        // ThemeMenuLabel and WindowTitle read from Localizer; refresh them when culture
        // flips so the menu/title text updates live alongside the {loc:Loc} bindings.
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeMenuLabel));
            OnPropertyChanged(nameof(WindowTitle));
        };

        // DispatcherTimer is null when the WPF dispatcher isn't running (e.g. unit tests).
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _updateTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = UpdateCheckInterval,
            };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync().ConfigureAwait(false);
            _updateTimer.Start();
        }
    }

    /// <summary>
    /// Polls GitHub for a newer release. Safe to call from any thread; publishes results
    /// onto the UI dispatcher when present. Silently no-ops on a non-installed (loose) build.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        UpdateAvailableInfo? info;
        try
        {
            info = await _updateService.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            return;
        }

        _availableUpdate = info;
        await DispatchAsync(() =>
        {
            IsUpdateAvailable = info is not null;
            AvailableVersion = info?.NewVersion;
            if (info is not null)
            {
                _logger.Log(this, LogType.Status, $"Update available: v{info.NewVersion} (current v{_updateService.CurrentVersion})");
            }
        });
    }

    private static Task DispatchAsync(Action action)
    {
        Dispatcher? d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return d.BeginInvoke(action).Task;
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
        _services.GetService<Services.TrayIconManager>()?.ShowMainWindow();
        SelectedTabIndex = 1; // Uploaded tab (order: Uploads, Uploaded, Settings, Logs).
    }

    public UploadsViewModel UploadsViewModel { get; }

    public UploadedViewModel UploadedViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public ConnectionManagerViewModel ConnectionManagerViewModel { get; }

    public LogsViewModel LogsViewModel { get; }

    public async Task InitializeAsync()
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
        ApplyTheme(value);

        // Re-apply the immersive dark title bar to every currently open window.
        // Newly opened windows pick this up via the global Window.Loaded handler.
        Lib.UI.ImmersiveDarkMode.SetIsDark(value);

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

    private static void ApplyTheme(bool dark)
    {
        Application app = Application.Current;
        if (app == null)
        {
            return;
        }

        Collection<ResourceDictionary> mergedDicts = app.Resources.MergedDictionaries;

        // Find and remove the current theme dictionary.
        ResourceDictionary? existingTheme = null;
        foreach (ResourceDictionary? dict in mergedDicts)
        {
            if (dict.Source != null &&
                (dict.Source.OriginalString.Contains("Theme.Light", StringComparison.Ordinal) ||
                 dict.Source.OriginalString.Contains("Theme.Dark", StringComparison.Ordinal)))
            {
                existingTheme = dict;
                break;
            }
        }

        if (existingTheme != null)
        {
            mergedDicts.Remove(existingTheme);
        }

        // Add the new theme dictionary.
        string themeFile = dark ? "Resources/Theme.Dark.xaml" : "Resources/Theme.Light.xaml";
        var newTheme = new ResourceDictionary
        {
            Source = new Uri(themeFile, UriKind.Relative),
        };
        mergedDicts.Add(newTheme);
    }

    private void Logger_OnLogOutput(object? sender, LogEvent e) => Application.Current?.Dispatcher.BeginInvoke(() => LogsViewModel.AddLogEntry(e));
}
