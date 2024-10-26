// <copyright file="SettingStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public class SettingStore : Store<SettingDbm>
    {
        public async Task<SettingDbm> FindByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindFirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        public async Task<SettingDbm> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindFirstOrDefaultAsync(s => s.Key.ToLower() == key.ToLower(), cancellationToken);
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(dbSetting => dbSetting.Id == id, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(dbSetting => ids.Contains(dbSetting.Id), cancellationToken);
        }
    }
}
