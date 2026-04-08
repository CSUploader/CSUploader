// <copyright file="UploadPackageFileRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class UploadPackageFileRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<UploadPackageFileDbm, UploadPackageFileDto>(dbFactory)
{
    public async Task<UploadPackageFileDto?> FindAsync(int fileId, CancellationToken cancellationToken = default)
    {
        UploadPackageFileDbm? entity = await FindFirstOrDefaultAsync(fu => fu.Id == fileId, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int fileId, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fu.Id == fileId, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fileIds.Contains(fu.Id), cancellationToken);

    protected override UploadPackageFileDto MapToDto(UploadPackageFileDbm entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FileDirectory = entity.FileDirectory,
        FileSize = entity.FileSize,
        FileHoster = entity.FileHoster,
        StartDateTime = entity.StartDateTime,
        FinishedDateTime = entity.FinishedDateTime,
        CompressionPassword = entity.CompressionPassword,
        FileUrl = entity.FileUrl,
        FileHosterName = entity.FileHosterName,
    };

    protected override void MapToDto(UploadPackageFileDbm entity, UploadPackageFileDto dto)
    {
        dto.Id = entity.Id;
        dto.FileName = entity.FileName;
        dto.FileDirectory = entity.FileDirectory;
        dto.FileSize = entity.FileSize;
        dto.FileHoster = entity.FileHoster;
        dto.StartDateTime = entity.StartDateTime;
        dto.FinishedDateTime = entity.FinishedDateTime;
        dto.CompressionPassword = entity.CompressionPassword;
        dto.FileUrl = entity.FileUrl;
        dto.FileHosterName = entity.FileHosterName;
    }

    protected override UploadPackageFileDbm MapToDbm(UploadPackageFileDto dto) => new()
    {
        Id = dto.Id,
        FileName = dto.FileName ?? string.Empty,
        FileDirectory = dto.FileDirectory ?? string.Empty,
        FileSize = dto.FileSize,
        FileHoster = dto.FileHoster ?? string.Empty,
        StartDateTime = dto.StartDateTime,
        FinishedDateTime = dto.FinishedDateTime,
        CompressionPassword = dto.CompressionPassword ?? string.Empty,
        FileUrl = dto.FileUrl ?? string.Empty,
        FileHosterName = dto.FileHosterName ?? string.Empty,
    };
}
