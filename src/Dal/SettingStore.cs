// <copyright file="SettingStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class SettingStore(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Store<SettingDbm>(dbFactory)
{
    public async Task<SettingDbm?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await FindFirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<SettingDbm?> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        return await FindFirstOrDefaultAsync(s => s.Key != null && s.Key.ToLower() == key.ToLower(), cancellationToken);
    }

    public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        return await DeleteAsync(dbSetting => dbSetting.Id == id, cancellationToken);
    }

    public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        return await DeleteAsync(dbSetting => ids.Contains(dbSetting.Id), cancellationToken);
    }
}
