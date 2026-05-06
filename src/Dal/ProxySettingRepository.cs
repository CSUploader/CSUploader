// <copyright file="ProxySettingRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class ProxySettingRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<ProxySettingDbm, ProxySettingDto>(dbFactory)
{
    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(p => p.Id == id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(p => ids.Contains(p.Id), cancellationToken);

    protected override ProxySettingDto MapToDto(ProxySettingDbm entity) => new()
    {
        Id = entity.Id,
        Type = (ProxyType)entity.Type,
        Host = entity.Host,
        Port = entity.Port,
        Username = entity.Username,
        Password = entity.Password,
        Enabled = entity.Enabled,
        Priority = entity.Priority,
    };

    protected override void MapToDto(ProxySettingDbm entity, ProxySettingDto dto)
    {
        dto.Id = entity.Id;
        dto.Type = (ProxyType)entity.Type;
        dto.Host = entity.Host;
        dto.Port = entity.Port;
        dto.Username = entity.Username;
        dto.Password = entity.Password;
        dto.Enabled = entity.Enabled;
        dto.Priority = entity.Priority;
    }

    protected override ProxySettingDbm MapToDbm(ProxySettingDto dto) => new()
    {
        Id = dto.Id,
        Type = (int)dto.Type,
        Host = dto.Host,
        Port = dto.Port,
        Username = dto.Username,
        Password = dto.Password,
        Enabled = dto.Enabled,
        Priority = dto.Priority,
    };
}
