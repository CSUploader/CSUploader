// <copyright file="MainForm_SettingsTabPage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Upload;

namespace CSUploader.Views
{
    public partial class MainForm : Form
    {
        public void MainForm_SettingsTabPage_Load(object sender, EventArgs e)
        {
            cbSettingSpeedLimitEnabled.CheckedChanged += CbSettingSpeedLimitEnabled_CheckedChanged;
            btnSettingsSave.Click += BtnSettingsSave_Click;

            nupSettingsMaxConcurrentCPUJobs.Value = Settings.MaxConcurrentCPUJobs;
            nupSettingsMaxConcurrentUploadJobs.Value = Settings.MaxConcurrentUploadJobs;
            if (Settings.SpeedLimit.HasValue)
            {
                nupSettingsSpeedLimit.Value = Settings.SpeedLimit.Value;
                cbSettingSpeedLimitEnabled.Checked = false;
            }
            else
            {
                nupSettingsSpeedLimit.Enabled = false;
                cbSettingSpeedLimitEnabled.Checked = true;
            }
        }

        private void MainForm_SettingsTabPage_Focus(object sender, EventArgs e)
        {
        }

        private void CbSettingSpeedLimitEnabled_CheckedChanged(object? sender, EventArgs e)
        {
            if (sender is not CheckBox checkBox)
            {
                return;
            }

            nupSettingsSpeedLimit.Enabled = checkBox.Checked;
        }

        private async void BtnSettingsSave_Click(object? sender, EventArgs e)
        {
            //string tempArchiveDirectory = tbSettingsTempArchiveDirectory.Text.Trim();
            //if (String.IsNullOrEmpty(tempArchiveDirectory))
            //{
            //    GUIHelper.Error(tbSettingsTempArchiveDirectory, "Temp archive directory can not be empty");
            //    return;
            //}

            int maxConcurrentCompressionJobs = (int)nupSettingsMaxConcurrentCPUJobs.Value;
            int maxConcurrentUploadJobs = (int)nupSettingsMaxConcurrentUploadJobs.Value;
            int? speedLimit = cbSettingSpeedLimitEnabled.Checked ? (int?)nupSettingsSpeedLimit.Value : null;

            await ProgressForm.ExecuteAsync(this, "Saving changes...", false, async (form, cancellationToken) =>
            {
                Dictionary<string, string> keyValues = new()
                {
                    //{ SettingKey.TempArchiveDirectory, tempArchiveDirectory}
                    { SettingKey.MaxConcurrentCPUJobs, maxConcurrentCompressionJobs.ToString() },
                    { SettingKey.MaxConcurrentUploadJobs, maxConcurrentUploadJobs.ToString() },
                    { SettingKey.SpeedLimit, speedLimit.HasValue ? speedLimit.Value.ToString() : string.Empty }
                };

                foreach (KeyValuePair<string, string> keyValue in keyValues)
                {
                    string key = keyValue.Key;
                    string value = keyValue.Value;
                    SettingDto? setting = await Database.FindSettingByKeyAsync(key, cancellationToken);
                    if (setting == null)
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            setting = new SettingDto
                            {
                                Key = key,
                                Value = value
                            };
                            await Database.InsertAsync(setting, cancellationToken);
                            Logger.Log(this, LogType.Status, $"Setting `{key}` with value `{value}` added");
                        }
                    }
                    else if (string.IsNullOrEmpty(value))
                    {
                        await Database.DeleteAsync(setting, cancellationToken);
                        Logger.Log(this, LogType.Status, $"Setting `{key}` deleted");
                    }
                    else
                    {
                        setting.Value = value;
                        await Database.UpdateAsync(setting, cancellationToken);
                        Logger.Log(this, LogType.Status, $"Setting `{key}` updated with value `{value}`");
                    }
                }
            });
            Logger.Log(this, LogType.Status, $"Settings saved");

            await ProgressForm.ExecuteAsync(this, "Refreshing settings...", false, async (form, cancellationToken) =>
            {
                await LoadSettingsAsync(cancellationToken);
            });
            Logger.Log(this, LogType.Status, $"Settings loaded");
        }

        private async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            // Get settings
            SettingDto[] settings = await Database.GetSettingsAsync(cancellationToken);

            // Temp archive directory
            string? tempArchiveDirectory = settings.Where(s => s.Key == SettingKey.TempArchiveDirectory).Select(s => s.Value).FirstOrDefault();
            if (string.IsNullOrEmpty(tempArchiveDirectory))
            {
                tempArchiveDirectory = Settings.DefaultTempArchiveDirectory;
            }

            Settings.TempArchiveDirectory = tempArchiveDirectory;

            // Max concurrent compression jobs
            int? maxConcurrentCompressionJobs = settings.Where(s => s.Key == SettingKey.MaxConcurrentCPUJobs).Select(s => string.IsNullOrEmpty(s.Value) ? null : (int?)int.Parse(s.Value)).FirstOrDefault();
            if (!maxConcurrentCompressionJobs.HasValue)
            {
                maxConcurrentCompressionJobs = Settings.DefaultMaxConcurrentCPUJobs;
            }

            Settings.MaxConcurrentCPUJobs = maxConcurrentCompressionJobs.Value;

            // Max concurrent upload jobs
            int? maxConcurrentUploadJobs = settings.Where(s => s.Key == SettingKey.MaxConcurrentUploadJobs).Select(s => string.IsNullOrEmpty(s.Value) ? null : (int?)int.Parse(s.Value)).FirstOrDefault();
            if (!maxConcurrentUploadJobs.HasValue)
            {
                maxConcurrentUploadJobs = Settings.DefaultMaxConcurrentUploadJobs;
            }

            Settings.MaxConcurrentUploadJobs = maxConcurrentUploadJobs.Value;

            // Speed limit
            Settings.SpeedLimit = settings.Where(s => s.Key == SettingKey.SpeedLimit).Select(s => string.IsNullOrEmpty(s.Value) ? null : (int?)int.Parse(s.Value)).FirstOrDefault();
        }
    }
}
