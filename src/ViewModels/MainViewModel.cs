// <copyright file="MainViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Lib;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private int selectedTabIndex;

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
        FirstRun.InitializeDatabase(_services);

        await SettingsViewModel.LoadAsync();
        await UploadViewModel.LoadFileHostersAsync();
        await UploadedViewModel.LoadAsync();
    }

    private void Logger_OnLogOutput(object? sender, LogEvent e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LogsViewModel.AddLogEntry(e);
        });
    }
}
