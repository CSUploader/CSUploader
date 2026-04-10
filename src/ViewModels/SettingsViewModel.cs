// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingRepository _settingRepository;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private int maxConcurrentCPUJobs = AppSettings.DefaultMaxConcurrentCPUJobs;

    [ObservableProperty]
    private int maxConcurrentUploadJobs = AppSettings.DefaultMaxConcurrentUploadJobs;

    [ObservableProperty]
    private bool speedLimitEnabled;

    [ObservableProperty]
    private int speedLimitValue;

    [ObservableProperty]
    private string tempArchiveDirectory = AppSettings.DefaultTempArchiveDirectory;

    [ObservableProperty]
    private string selectedCategory = "General";

    public SettingsViewModel(SettingRepository settingRepository, AppSettings settings, IDialogService dialogService)
    {
        _settingRepository = settingRepository;
        _settings = settings;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private void BrowseTempDirectory()
    {
        string? folder = _dialogService.BrowseFolder(TempArchiveDirectory, "Select Temp Archive Directory");
        if (folder is not null)
        {
            TempArchiveDirectory = folder;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        SettingDto[] settings = await _settingRepository.GetAllAsync(cancellationToken);

        foreach (SettingDto setting in settings)
        {
            switch (setting.Key)
            {
                case var k when k == SettingKey.MaxConcurrentCPUJobs:
                    if (int.TryParse(setting.Value, out int cpuJobs))
                    {
                        MaxConcurrentCPUJobs = cpuJobs;
                    }

                    break;

                case var k when k == SettingKey.MaxConcurrentUploadJobs:
                    if (int.TryParse(setting.Value, out int uploadJobs))
                    {
                        MaxConcurrentUploadJobs = uploadJobs;
                    }

                    break;

                case var k when k == SettingKey.SpeedLimit:
                    if (int.TryParse(setting.Value, out int speedLimit))
                    {
                        SpeedLimitValue = speedLimit;
                        SpeedLimitEnabled = speedLimit > 0;
                    }

                    break;

                case var k when k == SettingKey.TempArchiveDirectory:
                    if (!string.IsNullOrWhiteSpace(setting.Value))
                    {
                        TempArchiveDirectory = setting.Value;
                    }

                    break;
            }
        }

        // Apply loaded settings to runtime AppSettings
        _settings.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        _settings.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        _settings.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        _settings.TempArchiveDirectory = TempArchiveDirectory;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveSettingAsync(SettingKey.MaxConcurrentCPUJobs, MaxConcurrentCPUJobs.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await SaveSettingAsync(SettingKey.MaxConcurrentUploadJobs, MaxConcurrentUploadJobs.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await SaveSettingAsync(SettingKey.SpeedLimit, SpeedLimitEnabled ? SpeedLimitValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0", cancellationToken);
        await SaveSettingAsync(SettingKey.TempArchiveDirectory, TempArchiveDirectory, cancellationToken);

        // Apply to the running AppSettings instance
        _settings.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        _settings.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        _settings.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        _settings.TempArchiveDirectory = TempArchiveDirectory;
    }

    private async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        SettingDto? existing = await _settingRepository.FindByKeyAsync(key, cancellationToken);

        if (existing is not null)
        {
            existing.Value = value;
            await _settingRepository.UpdateAsync(existing, cancellationToken);
        }
        else
        {
            await _settingRepository.InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
    }
}
