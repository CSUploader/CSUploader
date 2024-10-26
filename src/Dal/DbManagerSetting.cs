// <copyright file="DbManagerSetting.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class DbManager
    {
        public async Task<SettingDto[]> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return await SettingManager.GetAllAsync(cancellationToken);
        }

        public async Task<SettingDto> FindSettingByKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            return await SettingManager.FindByKeyAsync(key, cancellationToken);
        }

        public async Task<int> InsertAsync(SettingDto setting, CancellationToken cancellationToken = default)
        {
            return await SettingManager.InsertAsync(setting, cancellationToken);
        }

        public async Task<int> UpdateAsync(SettingDto setting, CancellationToken cancellationToken = default)
        {
            return await SettingManager.UpdateAsync(setting, cancellationToken);
        }

        public virtual async Task<int> DeleteAsync(SettingDto setting, CancellationToken cancellationToken = default)
        {
            return await SettingManager.DeleteAsync(setting, cancellationToken);
        }

        public virtual async Task<int> DeleteAsync(IEnumerable<SettingDto> settings, CancellationToken cancellationToken = default)
        {
            return await SettingManager.DeleteAsync(settings, cancellationToken);
        }
    }
}
