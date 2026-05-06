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
using CSUploader.Lib.Update;
using CSUploader.Upload;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    // Index of the Settings tab in MainWindow.xaml's TabControl. If tabs are reordered
    // there, update this constant — the unsaved-changes prompt keys off it.
    private const int SettingsTabIndex = 2;

    private readonly IServiceProvider _services;
    private readonly IAppLogger _logger;
    private readonly IUpdateService _updateService;
    private readonly DispatcherTimer? _updateTimer;
    private UpdateAvailableInfo? _availableUpdate;
    private bool _suppressDarkModePersist;
    private bool _suppressTabRevert;

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

    public string ThemeMenuLabel => IsDarkMode ? "_Light Mode" : "_Dark Mode";

    public string WindowTitle => IsUpdateAvailable
        ? $"CSUploader — Update available (v{AvailableVersion}) — click Help → Install Update"
        : "CSUploader";

    [RelayCommand]
    private void ToggleTheme() => IsDarkMode = !IsDarkMode;

    /// <summary>
    /// Intercepts tab navigation away from Settings to warn about unsaved changes. The
    /// MVVM-Toolkit source generator emits this partial as a post-change hook, so the
    /// switch has already happened — if the user declines we flip the tab back. The
    /// revert is dispatched at Background priority because WPF's binding system drops
    /// re-entrant value changes that happen synchronously during a TabControl's own
    /// SelectionChanged handling. The guard prevents the deferred revert from
    /// re-triggering this same handler.
    /// </summary>
    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (_suppressTabRevert)
        {
            return;
        }

        if (oldValue == SettingsTabIndex && newValue != SettingsTabIndex
            && !SettingsViewModel.TryConfirmDiscardChanges())
        {
            Application.Current?.Dispatcher.BeginInvoke(
                () =>
                {
                    _suppressTabRevert = true;
                    try
                    {
                        SelectedTabIndex = oldValue;
                    }
                    finally
                    {
                        _suppressTabRevert = false;
                    }
                },
                DispatcherPriority.Background);
        }
    }

    public MainViewModel(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<IAppLogger>();
        _updateService = services.GetRequiredService<IUpdateService>();

        UploadsViewModel = services.GetRequiredService<UploadsViewModel>();
        UploadedViewModel = services.GetRequiredService<UploadedViewModel>();
        SettingsViewModel = services.GetRequiredService<SettingsViewModel>();
        ConnectionManagerViewModel = services.GetRequiredService<ConnectionManagerViewModel>();
        LogsViewModel = services.GetRequiredService<LogsViewModel>();

        _logger.OnLogOutput += Logger_OnLogOutput;

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

        Views.UpdateProgressWindow window = new()
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();

        Progress<int> progress = new(p => window.SetProgress(p));
        try
        {
            window.SetStatus($"Downloading update v{_availableUpdate.NewVersion}…");
            await _updateService.DownloadAsync(_availableUpdate, progress).ConfigureAwait(true);

            window.SetStatus("Restarting…");
            _updateService.ApplyAndRestart(_availableUpdate);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update install failed: {ex.Message}");
            window.SetStatus($"Update failed: {ex.Message}");
        }
    }

    public UploadsViewModel UploadsViewModel { get; }

    public UploadedViewModel UploadedViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public ConnectionManagerViewModel ConnectionManagerViewModel { get; }

    public LogsViewModel LogsViewModel { get; }

    public async Task InitializeAsync()
    {
        FirstRun.InitializeDatabase(_services, _logger);

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
