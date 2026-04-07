// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingManager _settingManager;

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

    public SettingsViewModel(SettingManager settingManager)
    {
        _settingManager = settingManager;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        SettingDto[] settings = await _settingManager.GetAllAsync(cancellationToken);

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
        AppSettings.Current.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        AppSettings.Current.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        AppSettings.Current.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        AppSettings.Current.TempArchiveDirectory = TempArchiveDirectory;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveSettingAsync(SettingKey.MaxConcurrentCPUJobs, MaxConcurrentCPUJobs.ToString(), cancellationToken);
        await SaveSettingAsync(SettingKey.MaxConcurrentUploadJobs, MaxConcurrentUploadJobs.ToString(), cancellationToken);
        await SaveSettingAsync(SettingKey.SpeedLimit, SpeedLimitEnabled ? SpeedLimitValue.ToString() : "0", cancellationToken);
        await SaveSettingAsync(SettingKey.TempArchiveDirectory, TempArchiveDirectory, cancellationToken);

        // Apply to the running AppSettings instance
        AppSettings.Current.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        AppSettings.Current.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        AppSettings.Current.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        AppSettings.Current.TempArchiveDirectory = TempArchiveDirectory;
    }

    private async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        SettingDto? existing = await _settingManager.FindByKeyAsync(key, cancellationToken);

        if (existing is not null)
        {
            existing.Value = value;
            await _settingManager.UpdateAsync(existing, cancellationToken);
        }
        else
        {
            await _settingManager.InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
    }
}
