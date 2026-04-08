// <copyright file="FileHosterLoginRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class FileHosterLoginRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<FileHosterLoginDbm, FileHosterLoginDto>(dbFactory)
{
    public async Task<FileHosterLoginDto?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm? entity = await FindFirstOrDefaultAsync(fh => fh.Id == id, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public async Task<FileHosterLoginDto[]> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm[] entities = await FindAsync(fh => fh.FileHosterName == name, cancellationToken);
        return entities.Select(MapToDto).ToArray();
    }

    public async Task<FileHosterLoginDto?> FindAsync(string name, string username, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm? entity = (await FindAsync(fh => fh.FileHosterName == name, cancellationToken))
            .FirstOrDefault(f => f.Username == username);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fh => fh.Id == id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fh => ids.Contains(fh.Id), cancellationToken);

    protected override FileHosterLoginDto MapToDto(FileHosterLoginDbm entity) => new()
    {
        Id = entity.Id,
        FileHosterName = entity.FileHosterName,
        Username = entity.Username,
        Password = entity.Password,
        Disabled = entity.Disabled,
        AccountType = entity.AccountType,
    };

    protected override void MapToDto(FileHosterLoginDbm entity, FileHosterLoginDto dto)
    {
        dto.Id = entity.Id;
        dto.FileHosterName = entity.FileHosterName;
        dto.Username = entity.Username;
        dto.Password = entity.Password;
        dto.Disabled = entity.Disabled;
        dto.AccountType = entity.AccountType;
    }

    protected override FileHosterLoginDbm MapToDbm(FileHosterLoginDto dto) => new()
    {
        Id = dto.Id,
        FileHosterName = dto.FileHosterName,
        Username = dto.Username ?? string.Empty,
        Password = dto.Password ?? string.Empty,
        Disabled = dto.Disabled,
        AccountType = dto.AccountType,
    };
}
