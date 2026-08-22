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
        SettingDbm? entity = await FindFirstOrDefaultAsync(s => s.Key != null && EF.Functions.Collate(s.Key, "NOCASE") == key, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    /// <summary>
    /// Writes <paramref name="value"/> for <paramref name="key"/>, inserting the row when it is not
    /// there yet. Lifted here out of SettingsViewModel, which owned the only copy: the upload wizard
    /// now records the last browsed folder too, and two hand-rolled find-then-insert-or-update
    /// blocks is one more than this deserves.
    /// </summary>
    public async Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        SettingDto? existing = await FindByKeyAsync(key, cancellationToken);
        if (existing is not null)
        {
            existing.Value = value;
            await UpdateAsync(existing, cancellationToken);
        }
        else
        {
            await InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
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
