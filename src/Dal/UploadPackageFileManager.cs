// <copyright file="UploadPackageFileManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public class UploadPackageFileManager(UploadPackageFileStore fileUploadStore)
    : StoreManager<UploadPackageFileDbm, UploadPackageFileDto, UploadPackageFileStore>(fileUploadStore)
{
    public async Task<UploadPackageFileDto?> FindAsync(int fileId, CancellationToken cancellationToken = default)
    {
        UploadPackageFileDbm? dbm = await Store.FindAsync(fileId, cancellationToken);
        return dbm is not null ? MapToDto(dbm) : null;
    }

    public Task<int> DeleteAsync(int fileId, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(fileId, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
        => Store.DeleteAsync(fileIds, cancellationToken);

    protected override UploadPackageFileDto MapToDto(UploadPackageFileDbm dbm) => new()
    {
        Id = dbm.Id,
        FileName = dbm.FileName,
        FileDirectory = dbm.FileDirectory,
        FileSize = dbm.FileSize,
        FileHoster = dbm.FileHoster,
        StartDateTime = dbm.StartDateTime,
        FinishedDateTime = dbm.FinishedDateTime,
        CompressionPassword = dbm.CompressionPassword,
        FileUrl = dbm.FileUrl,
        FileHosterName = dbm.FileHosterName,
    };

    protected override void MapToDto(UploadPackageFileDbm dbm, UploadPackageFileDto dto)
    {
        dto.Id = dbm.Id;
        dto.FileName = dbm.FileName;
        dto.FileDirectory = dbm.FileDirectory;
        dto.FileSize = dbm.FileSize;
        dto.FileHoster = dbm.FileHoster;
        dto.StartDateTime = dbm.StartDateTime;
        dto.FinishedDateTime = dbm.FinishedDateTime;
        dto.CompressionPassword = dbm.CompressionPassword;
        dto.FileUrl = dbm.FileUrl;
        dto.FileHosterName = dbm.FileHosterName;
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
