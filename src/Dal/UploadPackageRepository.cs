// <copyright file="UploadPackageRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class UploadPackageRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<UploadPackageDbm, UploadPackageDto>(dbFactory)
{
    protected override IQueryable<UploadPackageDbm> GetQuery(CSUploaderDbContext db) => db.Set<UploadPackageDbm>().Include(p => p.Files);

    public async Task<UploadPackageDto?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        UploadPackageDbm? entity = await FindFirstOrDefaultAsync(fu => fu.Id == id, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fu.Id == id, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => ids.Contains(fu.Id), cancellationToken);

    public async Task<UploadPackageDto[]> GetIncompleteAsync(CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        UploadPackageDbm[] entities = await GetQuery(db).Where(p => !p.IsCompleted).ToArrayAsync(ct);
        return [.. entities.Select(MapToDto)];
    }

    public async Task<UploadPackageDto[]> GetCompletedAsync(CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        UploadPackageDbm[] entities = await GetQuery(db).Where(p => p.IsCompleted).ToArrayAsync(ct);
        return [.. entities.Select(MapToDto)];
    }

    public async Task UpdateCompletedFlagAsync(int packageId, bool isCompleted, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCompleted, isCompleted), ct);
    }

    /// <summary>
    /// Hard-deletes the upload-history rows that are no longer visible in either tab — i.e.
    /// files soft-removed from Uploads and either soft-hidden from Uploaded or in a non-Completed
    /// terminal state. Then deletes any package row that has no remaining files. Visible rows
    /// (active queue + Uploaded-tab successes) are left untouched.
    /// </summary>
    public async Task<(int FilesDeleted, int PackagesDeleted)> DeleteHiddenHistoryAsync(CancellationToken ct = default)
    {
        int completed = (int)FileState.Completed;
        using CSUploaderDbContext db = DbFactory.CreateDbContext();

        // Files that are gone from BOTH tabs: removed from Uploads, AND either hidden from
        // Uploaded or never qualified for Uploaded (state != Completed).
        int filesDeleted = await db.Set<UploadPackageFileDbm>()
            .Where(f => f.IsRemovedFromUploads && (f.IsHidden || f.State != completed))
            .ExecuteDeleteAsync(ct);

        // Orphan packages: every file row has been deleted (either now or previously). Inner
        // join in GetDoneFilesWithPackageNameAsync would drop their files anyway, so removing
        // these doesn't lose any visible history.
        int packagesDeleted = await db.Set<UploadPackageDbm>()
            .Where(p => !db.Set<UploadPackageFileDbm>().Any(f => f.PackageId == p.Id))
            .ExecuteDeleteAsync(ct);

        return (filesDeleted, packagesDeleted);
    }

    protected override UploadPackageDto MapToDto(UploadPackageDbm entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        CreatedDateTime = entity.CreatedDateTime,
        ScheduledStartTime = entity.ScheduledStartTime,
        IsCompleted = entity.IsCompleted,
        SpeedLimitKBps = entity.SpeedLimitKBps,
        StartMode = (UploadStartMode)entity.StartMode,
        Priority = (PackagePriority)entity.Priority,
        IsRemovedFromUploads = entity.IsRemovedFromUploads,
        Files = entity.Files.Select(f => new UploadPackageFileDto
        {
            Id = f.Id,
            FileName = f.FileName,
            FileDirectory = f.FileDirectory,
            FileSize = f.FileSize,
            FileHoster = f.FileHoster,
            StartDateTime = f.StartDateTime,
            FinishedDateTime = f.FinishedDateTime,
            FileUrl = f.FileUrl,
            FileHosterName = f.FileHosterName,
            FileHosterAccount = f.FileHosterAccount,
            State = (FileState)f.State,
            Error = f.Error,
            IsHashingComplete = f.IsHashingComplete,
            FileHash = f.FileHash,
            FileHosterLoginId = f.FileHosterLoginId,
            SortOrder = f.SortOrder,
            PackageId = f.PackageId,
            IsHidden = f.IsHidden,
            IsRemovedFromUploads = f.IsRemovedFromUploads,
        }).ToArray(),
    };

    protected override void MapToDto(UploadPackageDbm entity, UploadPackageDto dto)
    {
        dto.Id = entity.Id;
        dto.Name = entity.Name;
        dto.CreatedDateTime = entity.CreatedDateTime;
        dto.ScheduledStartTime = entity.ScheduledStartTime;
        dto.IsCompleted = entity.IsCompleted;
        dto.SpeedLimitKBps = entity.SpeedLimitKBps;
        dto.StartMode = (UploadStartMode)entity.StartMode;
        dto.Priority = (PackagePriority)entity.Priority;
        dto.IsRemovedFromUploads = entity.IsRemovedFromUploads;
    }

    protected override UploadPackageDbm MapToDbm(UploadPackageDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name ?? string.Empty,
        CreatedDateTime = dto.CreatedDateTime,
        ScheduledStartTime = dto.ScheduledStartTime,
        IsCompleted = dto.IsCompleted,
        SpeedLimitKBps = dto.SpeedLimitKBps,
        StartMode = (int)dto.StartMode,
        Priority = (int)dto.Priority,
        IsRemovedFromUploads = dto.IsRemovedFromUploads,
    };

    /// <summary>
    /// Soft-removes a package from the Uploads tab. Its file rows stay in the DB so
    /// the Uploaded tab keeps showing them — until the user removes them there too,
    /// which sets each file's <see cref="UploadPackageFileDbm.IsHidden"/>.
    /// </summary>
    public async Task SoftRemoveFromUploadsAsync(int packageId, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsRemovedFromUploads, true), ct);
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.PackageId == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsRemovedFromUploads, true), ct);
    }

    /// <summary>
    /// Updates only the <see cref="UploadPackageDbm.Priority"/> column for a single
    /// package row. Targeted so it doesn't touch the Files navigation property.
    /// </summary>
    public async Task UpdatePriorityAsync(int packageId, PackagePriority priority, CancellationToken ct = default)
    {
        int value = (int)priority;
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Priority, value), ct);
    }
}
