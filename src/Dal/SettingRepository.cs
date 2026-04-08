// <copyright file="SettingRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class SettingRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<SettingDbm, SettingDto>(dbFactory)
{
    public async Task<SettingDto?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        SettingDbm? entity = await FindFirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public async Task<SettingDto?> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        SettingDbm? entity = await FindFirstOrDefaultAsync(s => s.Key != null && s.Key.ToLower() == key.ToLower(), cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(dbSetting => dbSetting.Id == id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(dbSetting => ids.Contains(dbSetting.Id), cancellationToken);

    protected override SettingDto MapToDto(SettingDbm entity) => new()
    {
        Id = entity.Id,
        Key = entity.Key,
        Value = entity.Value,
    };

    protected override void MapToDto(SettingDbm entity, SettingDto dto)
    {
        dto.Id = entity.Id;
        dto.Key = entity.Key;
        dto.Value = entity.Value;
    }

    protected override SettingDbm MapToDbm(SettingDto dto) => new()
    {
        Id = dto.Id,
        Key = dto.Key ?? string.Empty,
        Value = dto.Value ?? string.Empty,
    };
}
