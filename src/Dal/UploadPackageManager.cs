// <copyright file="UploadPackageManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class UploadPackageManager(UploadPackageStore uploadPackageStore)
    : StoreManager<UploadPackageDbm, UploadPackageDto, UploadPackageStore>(uploadPackageStore)
{
    public async Task<UploadPackageDto?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        UploadPackageDbm? dbm = await Store.FindAsync(id, cancellationToken);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(ids, cancellationToken);

    protected override UploadPackageDto MapToDto(UploadPackageDbm dbm) => new()
    {
        Id = dbm.Id,
        Name = dbm.Name,
        Files = dbm.Files.Select(f => new UploadPackageFileDto
        {
            Id = f.Id,
            FileName = f.FileName,
            FileDirectory = f.FileDirectory,
            FileSize = f.FileSize,
            FileHoster = f.FileHoster,
            StartDateTime = f.StartDateTime,
            FinishedDateTime = f.FinishedDateTime,
            CompressionPassword = f.CompressionPassword,
            FileUrl = f.FileUrl,
            FileHosterName = f.FileHosterName,
        }).ToArray(),
    };

    protected override void MapToDto(UploadPackageDbm dbm, UploadPackageDto dto)
    {
        dto.Id = dbm.Id;
        dto.Name = dbm.Name;
    }

    protected override UploadPackageDbm MapToDbm(UploadPackageDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name ?? string.Empty,
    };
}
