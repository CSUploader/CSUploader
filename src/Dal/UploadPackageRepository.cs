// <copyright file="UploadPackageRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class UploadPackageRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<UploadPackageDbm, UploadPackageDto>(dbFactory)
{
    protected override IQueryable<UploadPackageDbm> GetQuery(CSUploaderDbContext db)
    {
        return db.Set<UploadPackageDbm>().Include(p => p.Files);
    }

    public async Task<UploadPackageDto?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        UploadPackageDbm? entity = await FindFirstOrDefaultAsync(fu => fu.Id == id, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fu.Id == id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => ids.Contains(fu.Id), cancellationToken);

    protected override UploadPackageDto MapToDto(UploadPackageDbm entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Files = entity.Files.Select(f => new UploadPackageFileDto
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

    protected override void MapToDto(UploadPackageDbm entity, UploadPackageDto dto)
    {
        dto.Id = entity.Id;
        dto.Name = entity.Name;
    }

    protected override UploadPackageDbm MapToDbm(UploadPackageDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name ?? string.Empty,
    };
}
