// <copyright file="UploadPackageFileRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
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

    /// <summary>
    /// Soft-deletes files by flipping <see cref="UploadPackageFileDbm.IsHidden"/> to true.
    /// The rows remain in the database so history is preserved.
    /// </summary>
    public async Task<int> HideAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<UploadPackageFileDbm>()
            .Where(f => fileIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsHidden, true), cancellationToken);
    }

    /// <summary>
    /// Returns every file that has successfully uploaded together with its owning package
    /// name, regardless of whether the package itself has been marked <c>IsCompleted</c>.
    /// Used by the Uploaded tab so files appear as soon as they finish, not only when the
    /// whole package finishes. Failed/Cancelled rows are intentionally excluded — those have
    /// no URL and the user re-tries them from the Uploads tab; the Uploaded tab is the
    /// "successful uploads with URLs" history.
    /// </summary>
    public async Task<(UploadPackageFileDto File, string PackageName)[]> GetDoneFilesWithPackageNameAsync(CancellationToken ct = default)
    {
        int completed = (int)FileState.Completed;

        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        var rows = await db.Set<UploadPackageFileDbm>()
            .Where(f => f.State == completed && !f.IsHidden)
            .Join(
                db.Set<UploadPackageDbm>(),
                f => f.PackageId,
                p => p.Id,
                (f, p) => new { File = f, PackageName = p.Name })
            .ToArrayAsync(ct);

        return [.. rows.Select(r => (MapToDto(r.File), r.PackageName ?? string.Empty))];
    }

    public async Task UpdateStateAsync(int fileId, int state, string? error, string? fileUrl, DateTime? finishedDateTime = null, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        if (finishedDateTime is { } finished)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, state)
                    .SetProperty(f => f.Error, error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty)
                    .SetProperty(f => f.FinishedDateTime, finished), ct);
        }
        else
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, state)
                    .SetProperty(f => f.Error, error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty), ct);
        }
    }

    /// <summary>
    /// Persists the hex-encoded hash + the IsHashingComplete flag once a hoster's pre-upload
    /// hashing pass finishes successfully. Separate from <see cref="UpdateStateAsync"/> so we
    /// don't widen its signature for a column only some hosters touch.
    /// </summary>
    public async Task UpdateHashAsync(int fileId, string fileHash, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.FileHash, fileHash)
                .SetProperty(f => f.IsHashingComplete, true), ct);
    }

    public async Task UpdateFinishedAsync(int fileId, DateTime finishedDateTime, string? fileUrl, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.FinishedDateTime, finishedDateTime)
                .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty), ct);
    }

    protected override UploadPackageFileDto MapToDto(UploadPackageFileDbm entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FileDirectory = entity.FileDirectory,
        FileSize = entity.FileSize,
        FileHoster = entity.FileHoster,
        StartDateTime = entity.StartDateTime,
        FinishedDateTime = entity.FinishedDateTime,
        FileUrl = entity.FileUrl,
        FileHosterName = entity.FileHosterName,
        State = (FileState)entity.State,
        Error = entity.Error,
        IsHashingComplete = entity.IsHashingComplete,
        FileHash = entity.FileHash,
        FileHosterLoginId = entity.FileHosterLoginId,
        Priority = entity.Priority,
        SortOrder = entity.SortOrder,
        PackageId = entity.PackageId,
        IsHidden = entity.IsHidden,
        IsRemovedFromUploads = entity.IsRemovedFromUploads,
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
        dto.FileUrl = entity.FileUrl;
        dto.FileHosterName = entity.FileHosterName;
        dto.State = (FileState)entity.State;
        dto.Error = entity.Error;
        dto.IsHashingComplete = entity.IsHashingComplete;
        dto.FileHash = entity.FileHash;
        dto.FileHosterLoginId = entity.FileHosterLoginId;
        dto.Priority = entity.Priority;
        dto.SortOrder = entity.SortOrder;
        dto.PackageId = entity.PackageId;
        dto.IsHidden = entity.IsHidden;
        dto.IsRemovedFromUploads = entity.IsRemovedFromUploads;
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
        FileUrl = dto.FileUrl ?? string.Empty,
        FileHosterName = dto.FileHosterName ?? string.Empty,
        State = (int)dto.State,
        Error = dto.Error,
        IsHashingComplete = dto.IsHashingComplete,
        FileHash = dto.FileHash,
        FileHosterLoginId = dto.FileHosterLoginId,
        Priority = dto.Priority,
        SortOrder = dto.SortOrder,
        PackageId = dto.PackageId,
        IsHidden = dto.IsHidden,
        IsRemovedFromUploads = dto.IsRemovedFromUploads,
    };

    /// <summary>
    /// Soft-removes one or more files from the Uploads tab. The Uploaded tab keeps
    /// showing them (it filters by <see cref="UploadPackageFileDbm.IsHidden"/>, not
    /// <see cref="UploadPackageFileDbm.IsRemovedFromUploads"/>).
    /// </summary>
    public async Task<int> SoftRemoveFromUploadsAsync(IEnumerable<int> fileIds, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<UploadPackageFileDbm>()
            .Where(f => fileIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsRemovedFromUploads, true), ct);
    }
}
