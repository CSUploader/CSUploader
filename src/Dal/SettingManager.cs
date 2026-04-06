// <copyright file="SettingManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class SettingManager(SettingStore settingStore)
    : StoreManager<SettingDbm, SettingDto, SettingStore>(settingStore)
{
    public async Task<SettingDto?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        SettingDbm? dbm = await Store.FindByIdAsync(id, cancellationToken);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public async Task<SettingDto?> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        SettingDbm? dbm = await Store.FindByKeyAsync(key, cancellationToken);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(ids, cancellationToken);

    protected override SettingDto MapToDto(SettingDbm dbm) => new()
    {
        Id = dbm.Id,
        Key = dbm.Key,
        Value = dbm.Value,
    };

    protected override void MapToDto(SettingDbm dbm, SettingDto dto)
    {
        dto.Id = dbm.Id;
        dto.Key = dbm.Key;
        dto.Value = dbm.Value;
    }

    protected override SettingDbm MapToDbm(SettingDto dto) => new()
    {
        Id = dto.Id,
        Key = dto.Key ?? string.Empty,
        Value = dto.Value ?? string.Empty,
    };
}
