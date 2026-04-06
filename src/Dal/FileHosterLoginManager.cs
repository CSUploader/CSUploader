// <copyright file="FileHosterLoginManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class FileHosterLoginManager(FileHosterLoginStore fileHosterStore)
    : StoreManager<FileHosterLoginDbm, FileHosterLoginDto, FileHosterLoginStore>(fileHosterStore)
{
    public async Task<FileHosterLoginDto?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm? dbm = await Store.FindAsync(id, cancellationToken);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public async Task<FileHosterLoginDto[]> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm[] dbms = await Store.FindAsync(name, cancellationToken);
        return dbms.Select(MapToDto).ToArray();
    }

    public async Task<FileHosterLoginDto?> FindAsync(string name, string username, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDbm? dbm = (await Store.FindAsync(name, cancellationToken)).FirstOrDefault(f => f.Username == username);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(ids, cancellationToken);

    protected override FileHosterLoginDto MapToDto(FileHosterLoginDbm dbm) => new()
    {
        Id = dbm.Id,
        FileHosterName = dbm.FileHosterName,
        Username = dbm.Username,
        Password = dbm.Password,
        Disabled = dbm.Disabled,
        AccountType = dbm.AccountType,
    };

    protected override void MapToDto(FileHosterLoginDbm dbm, FileHosterLoginDto dto)
    {
        dto.Id = dbm.Id;
        dto.FileHosterName = dbm.FileHosterName;
        dto.Username = dbm.Username;
        dto.Password = dbm.Password;
        dto.Disabled = dbm.Disabled;
        dto.AccountType = dbm.AccountType;
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
