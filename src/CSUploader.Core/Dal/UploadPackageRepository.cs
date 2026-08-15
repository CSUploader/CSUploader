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

    /// <summary>
    /// Inserts a package and all of its file rows in one save — so the whole graph lands, or none
    /// of it does. Generated ids are written back into the dtos (package id and each file's id and
    /// PackageId, in order).
    /// </summary>
    /// <remarks>
    /// Inserted separately (package first, then one insert per file, each on its own context), a
    /// failure partway left a package row with only some of its files — and since the surviving
    /// exception was logged-and-swallowed, the missing files simply had no rows: their uploads ran,
    /// but every transition they tried to persist was discarded for lack of a DbId, so they
    /// vanished on restart. A single <c>SaveChangesAsync</c> is one transaction; EF inserts the
    /// package before its children and fixes up their PackageId itself.
    /// </remarks>
    public async Task InsertWithFilesAsync(UploadPackageDto package, IReadOnlyList<UploadPackageFileDto> files, CancellationToken ct = default)
    {
        UploadPackageDbm packageDbm = MapToDbm(package);
        UploadPackageFileDbm[] fileDbms = [.. files.Select(UploadPackageFileRepository.ToDbm)];
        foreach (UploadPackageFileDbm fileDbm in fileDbms)
        {
            packageDbm.Files.Add(fileDbm);
        }

        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<UploadPackageDbm>().Add(packageDbm);
        await db.SaveChangesAsync(ct);

        package.Id = packageDbm.Id;
        for (int i = 0; i < fileDbms.Length; i++)
        {
            files[i].Id = fileDbms[i].Id;
            files[i].PackageId = packageDbm.Id;
        }
    }

    public async Task UpdateCompletedFlagAsync(int packageId, bool isCompleted, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCompleted, isCompleted), ct);
    }

    /// <summary>Persists a package rename (the Uploads tab's editable Name cell). Single-field
    /// ExecuteUpdate like the completed-flag setter. The History tab reads package names through the
    /// load-time join against this row, so a reload picks the new name up automatically.</summary>
    public async Task UpdateNameAsync(int packageId, string name, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name), ct);
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

        // One transaction, like the other multi-statement writes. Half a cleanup is at least
        // self-healing (the next run would sweep the orphans), but there is no reason to leave
        // the window open when closing it costs two lines.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

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

        await tx.CommitAsync(ct);
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

        // One transaction: the package flag landing without the file flags would leave rows the
        // Uploads tab has dropped but the loader still restores as live files on the next start.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Set<UploadPackageDbm>()
            .Where(p => p.Id == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsRemovedFromUploads, true), ct);
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.PackageId == packageId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsRemovedFromUploads, true), ct);
        await tx.CommitAsync(ct);
    }

}
