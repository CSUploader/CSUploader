// <copyright file="MainViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Lib;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    private bool isDarkMode;

    public MainViewModel(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<IAppLogger>();

        UploadViewModel = services.GetRequiredService<UploadViewModel>();
        UploadViewModel.SwitchToTab = index => SelectedTabIndex = index;
        UploadsViewModel = services.GetRequiredService<UploadsViewModel>();
        UploadedViewModel = services.GetRequiredService<UploadedViewModel>();
        SettingsViewModel = services.GetRequiredService<SettingsViewModel>();
        LogsViewModel = services.GetRequiredService<LogsViewModel>();

        _logger.OnLogOutput += Logger_OnLogOutput;
    }

    public UploadViewModel UploadViewModel { get; }

    public UploadsViewModel UploadsViewModel { get; }

    public UploadedViewModel UploadedViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public LogsViewModel LogsViewModel { get; }

    public async Task InitializeAsync()
    {
        FirstRun.InitializeDatabase(_services, _logger);

        await SettingsViewModel.LoadAsync();
        await UploadViewModel.LoadFileHostersAsync();
        await UploadedViewModel.LoadAsync();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;
        ApplyTheme(IsDarkMode);
    }

    private static void ApplyTheme(bool dark)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var mergedDicts = app.Resources.MergedDictionaries;

        // Find and remove the current theme dictionary.
        ResourceDictionary? existingTheme = null;
        foreach (var dict in mergedDicts)
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

    private void Logger_OnLogOutput(object? sender, LogEvent e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogsViewModel.AddLogEntry(e);
        });
    }
}
