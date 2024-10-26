// <copyright file="SettingManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public class SettingManager
        : StoreManager<SettingDbm, SettingDto, SettingStore>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingManager"/> class.
        /// </summary>
        /// <param name="settingStore">The setting store.</param>
        public SettingManager(SettingStore settingStore)
            : base(settingStore)
        {
        }

        public async Task<SettingDto> FindByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SettingDbm dbm = await Store.FindByIdAsync(id, cancellationToken);
            return Map(dbm);
        }

        public async Task<SettingDto> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            SettingDbm dbm = await Store.FindByKeyAsync(key, cancellationToken);
            return Map(dbm);
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(id, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(ids, cancellationToken);
        }
    }
}
